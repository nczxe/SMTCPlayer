using System.Threading.Channels;
using SMTCPlayer.Logging;
using SMTCPlayer.PluginApi;

namespace SMTCPlayer.PluginSystem;

/// <summary>
/// 插件事件广播池：有界通道（DropOldest）+ 单读者顺序分发。
/// <para>
/// Publish 非阻塞且永不抛异常；订阅者处理过慢导致通道满时丢弃最老事件，
/// 分发侧按"写入计数 − 读取计数 − 当前在队"精确统计丢弃数（<see cref="DroppedCount"/>），
/// 该式与事件到达顺序无关，不会因并发发布的乱序而虚增。
/// 订阅者异常彼此隔离：单个订阅者抛异常仅记 Warn，不影响其他订阅者与分发循环。
/// </para>
/// </summary>
public sealed class PluginBroadcastPool : IAsyncDisposable
{
    private const int ChannelCapacity = 256;
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(3);
    private const long WarnEveryDropped = 10;

    private readonly ILog _log;
    private readonly object _sync = new();
    private readonly List<IBroadcastSubscriber> _subscribers = [];
    private readonly Channel<PluginEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _dispatchTask;

    private long _written;           // 发布侧写入计数（每 Publish 一条 +1）
    private long _read;              // 分发侧读取计数（每取出并分发一条 +1）
    private long _lastWarnedDropped; // 仅分发循环单一读者访问，用于警告节流
    private int _completed;
    private int _disposed;

    /// <summary>创建广播池并立即启动后台分发循环。</summary>
    /// <param name="logCategory">日志分类；缺省为 "PluginSystem"。</param>
    public PluginBroadcastPool(string? logCategory = null)
    {
        _log = LogManager.GetLogger(logCategory ?? "PluginSystem");
        _channel = Channel.CreateBounded<PluginEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // 拥塞丢最老，由分发侧按写入/读取计数差统计
            SingleReader = true,
        });
        _dispatchTask = Task.Run(DispatchLoopAsync);
    }

    /// <summary>
    /// 累计被丢弃的事件数（通道满时按 DropOldest 丢弃，仅供诊断）。
    /// 精确式：写入数 − 读取数 − 当前在队数；与到达顺序无关，并发乱序也不会虚增。
    /// </summary>
    public long DroppedCount
    {
        get
        {
            var queued = _channel.Reader.Count; // 有界通道支持精确在队计数
            var dropped = Interlocked.Read(ref _written) - Interlocked.Read(ref _read) - queued;
            return dropped > 0 ? dropped : 0;
        }
    }

    /// <summary>
    /// 注册订阅者，返回退订句柄。同一实例重复注册被忽略（记 Warn，返回空句柄）。
    /// 注册/退订在下一次事件分发时生效（分发循环逐事件取订阅者快照）。
    /// </summary>
    public IDisposable Subscribe(IBroadcastSubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);

        lock (_sync)
        {
            if (_subscribers.Contains(subscriber))
            {
                _log.Warn($"订阅者重复注册，已忽略: {subscriber.SubscriberId ?? "<未命名>"}");
                return NoopSubscription.Instance;
            }

            _subscribers.Add(subscriber);
        }

        return new Subscription(this, subscriber);
    }

    /// <summary>写入一条事件（非阻塞，永不抛异常）。</summary>
    public void Publish(PluginEvent pluginEvent)
    {
        try
        {
            Interlocked.Increment(ref _written);
            _channel.Writer.TryWrite(pluginEvent);
        }
        catch (Exception ex)
        {
            _log.Error("广播池 Publish 单条事件异常", ex);
        }
    }

    /// <summary>批量写入事件（非阻塞，永不抛异常）。</summary>
    public void Publish(IEnumerable<PluginEvent> events)
    {
        try
        {
            foreach (var evt in events)
            {
                Interlocked.Increment(ref _written);
                _channel.Writer.TryWrite(evt);
            }
        }
        catch (Exception ex)
        {
            _log.Error("广播池 Publish 批量事件异常", ex);
        }
    }

    /// <summary>
    /// 完成写端（关闭通道）：分发循环把剩余已入队事件分发完毕后自然退出。幂等。
    /// 注意此处不取消分发令牌——已入队事件仍需被分发完。
    /// </summary>
    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 1) return;
        try { _channel.Writer.TryComplete(); }
        catch { /* 关闭永不抛异常 */ }
    }

    /// <summary>Complete 后限时（3 秒）等待分发循环排空退出；超时记 Warn 并强制取消。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        Complete();
        try
        {
            // Complete 后 ReadAllAsync 会先把已入队事件全部送出，随后循环正常结束
            var finished = await Task.WhenAny(_dispatchTask, Task.Delay(DisposeTimeout)).ConfigureAwait(false);
            if (finished != _dispatchTask)
            {
                _log.Warn($"广播池等待分发循环退出超时（{DisposeTimeout.TotalSeconds:0}s），强制取消剩余分发");
                try { _cts.Cancel(); } catch { /* 取消永不抛异常 */ }
            }
        }
        catch (Exception ex)
        {
            _log.Error("广播池 DisposeAsync 异常", ex);
        }
        finally
        {
            // 循环已退出（或已超时强制取消）后取消令牌，向残留监听方广播关闭信号
            try { _cts.Cancel(); } catch { /* 取消永不抛异常 */ }
        }

        var dropped = DroppedCount;
        if (dropped > 0)
            _log.Warn($"广播池已关闭，累计丢弃事件 {dropped} 条");
    }

    // ===== 分发循环 =====

    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (var pluginEvent in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                // 精确丢弃统计：写入数 − 读取数 − 当前在队数。
                // DropOldest 下 TryWrite 恒成功，无法用其返回值统计；但写入计数恒增，
                // 读取计数只随实际取出的条数增，二者之差再减去仍在队列中的条数即被丢弃数。
                // 该式与到达顺序无关，不会因并发发布的乱序而产生虚增缺口。
                Interlocked.Increment(ref _read);
                var dropped = Interlocked.Read(ref _written) - Interlocked.Read(ref _read) - _channel.Reader.Count;
                if (dropped > _lastWarnedDropped && dropped / WarnEveryDropped > _lastWarnedDropped / WarnEveryDropped)
                {
                    // 汇总节流：累计每跨过 10 的倍数输出一次，不逐条刷日志
                    _lastWarnedDropped = dropped;
                    _log.Warn($"广播池事件被丢弃 {dropped} 条（订阅者处理过慢）");
                }

                // 每轮取订阅者快照：订阅/退订在下一轮生效
                List<IBroadcastSubscriber> snapshot;
                lock (_sync) snapshot = [.. _subscribers];

                foreach (var subscriber in snapshot)
                {
                    try
                    {
                        await subscriber.HandleEventAsync(pluginEvent, _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // 关闭信号：直接结束分发循环
                    }
                    catch (Exception ex)
                    {
                        // 异常隔离：单订阅者异常不影响其他订阅者与循环
                        _log.Warn($"订阅者 {subscriber.SubscriberId ?? "<未命名>"} 处理 {pluginEvent.Type} 事件异常: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭（DisposeAsync 超时强制取消）
        }
        catch (Exception ex)
        {
            _log.Error("广播池分发循环异常退出", ex);
        }
    }

    // ===== 订阅句柄 =====

    private sealed class Subscription(PluginBroadcastPool pool, IBroadcastSubscriber subscriber) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            lock (pool._sync)
            {
                pool._subscribers.Remove(subscriber);
            }
        }
    }

    private sealed class NoopSubscription : IDisposable
    {
        public static readonly NoopSubscription Instance = new();
        private NoopSubscription() { }
        public void Dispose() { }
    }
}
