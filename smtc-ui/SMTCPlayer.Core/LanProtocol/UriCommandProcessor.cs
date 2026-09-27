using System.Globalization;
using SMTCPlayer.Core.Services;

namespace SMTCPlayer.Core.LanProtocol;

/// <summary>解析后的 URI 命令。<see cref="Action"/> 为归一化后的动作名。</summary>
public readonly record struct UriCommand(string Action, double? Volume);

/// <summary>
/// <c>smtcplayer://</c> 命令的解析与执行器。
/// <para>
/// 支持命令：<c>play/pause/toggle/next/previous/mute/show</c> 与 <c>volume?value=0-100</c>。
/// 后端未就绪时命令进入<b>有界待执行队列</b>：上限 <see cref="MaxQueueSize"/> 条（满时丢弃最旧并记 Warn）、
/// 单条 <see cref="CommandTimeout"/> 超时作废（记 Warn）；后端就绪后按入队顺序执行。
/// </para>
/// <para><c>show</c> 不依赖后端，立即置前窗口。<c>volume</c> 取值 0~100。</para>
/// </summary>
public sealed class UriCommandProcessor : IDisposable
{
    /// <summary>待执行队列上限，超出时丢弃最旧一条。</summary>
    public const int MaxQueueSize = 16;

    /// <summary>单条命令的最大排队时长，超过即作废。</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    private readonly LanCommandDispatcher _dispatcher;
    private readonly Action? _showAction;
    private readonly LinkedList<PendingCommand> _queue = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _execGate = new(1, 1);
    private readonly Timer _sweepTimer;

    private volatile bool _backendReady;
    private int _disposed;

    private sealed record PendingCommand(UriCommand Command, DateTime EnqueuedAtUtc);

    public UriCommandProcessor(SmtcApiClient api, Action? showAction = null)
    {
        if (api == null) throw new ArgumentNullException(nameof(api));
        _dispatcher = new LanCommandDispatcher(api);
        _showAction = showAction;
        _sweepTimer = new Timer(_ => SweepExpired(), null, SweepInterval, SweepInterval);
    }

    /// <summary>当前排队中的命令数量。</summary>
    public int PendingCount
    {
        get { lock (_gate) return _queue.Count; }
    }

    /// <summary>后端就绪：按入队顺序执行队列中未超时的命令。</summary>
    public void MarkBackendReady()
    {
        // 置"就绪"与排空必须在同一把锁内完成，与 Enqueue 的就绪判定/入队互斥，
        // 否则存在 TOCTOU：Enqueue 读到"未就绪"后、真正入队前，本方法已排空并返回，
        // 导致该命令被永久滞留（直到超时作废）。
        List<PendingCommand>? drained = null;
        lock (_gate)
        {
            _backendReady = true;
            if (_queue.Count > 0)
            {
                drained = new List<PendingCommand>(_queue);
                _queue.Clear();
            }
        }

        if (drained == null) return;

        _ = Task.Run(async () =>
        {
            foreach (var pending in drained)
            {
                if (IsExpired(pending))
                {
                    Logger.Warn($"URI 命令在排队期间超时作废（>{CommandTimeout.TotalSeconds:F0}s）: {pending.Command.Action}");
                    continue;
                }
                await ExecuteAsync(pending.Command).ConfigureAwait(false);
            }
        });
    }

    /// <summary>后端停止：后续命令进入队列等待。</summary>
    public void MarkBackendNotReady()
    {
        lock (_gate) _backendReady = false;
    }

    /// <summary>把解析后的命令交给处理器：<c>show</c> 立即执行，其余按就绪状态执行或入队。</summary>
    public void Enqueue(UriCommand command)
    {
        if (string.Equals(command.Action, "show", StringComparison.OrdinalIgnoreCase))
        {
            try { _showAction?.Invoke(); }
            catch (Exception ex) { Logger.Warn($"置前窗口失败: {ex.Message}"); }
            return;
        }

        // 就绪判定与入队必须在同一把锁内完成，与 MarkBackendReady 的"置就绪 + 排空"互斥，
        // 否则存在 TOCTOU：读到"未就绪"后、真正入队前，MarkBackendReady 已排空并返回，
        // 该命令将永久滞留队列（直到超时作废）。
        bool executeNow;
        lock (_gate)
        {
            executeNow = _backendReady;
            if (!executeNow)
            {
                while (_queue.Count >= MaxQueueSize)
                {
                    var oldest = _queue.First!.Value;
                    _queue.RemoveFirst();
                    Logger.Warn($"URI 命令队列已满（上限 {MaxQueueSize}），丢弃最旧命令: {oldest.Command.Action}");
                }
                _queue.AddLast(new PendingCommand(command, DateTime.UtcNow));
            }
        }

        if (executeNow)
        {
            _ = ExecuteAsync(command);
            return;
        }

        Logger.Info($"URI 命令已入队等待后端就绪: {command.Action}");
    }

    /// <summary>解析形如 <c>smtcplayer://next</c> / <c>smtcplayer://volume?value=50</c> 的 URI。</summary>
    public static bool TryParse(string? uri, out UriCommand command)
    {
        command = default;
        if (string.IsNullOrWhiteSpace(uri)) return false;

        var text = uri.Trim();
        var prefix = UriScheme.Name + ":";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = text[prefix.Length..];

        var fragmentIndex = rest.IndexOf('#');
        if (fragmentIndex >= 0) rest = rest[..fragmentIndex];

        string? query = null;
        var queryIndex = rest.IndexOf('?');
        if (queryIndex >= 0)
        {
            query = rest[(queryIndex + 1)..];
            rest = rest[..queryIndex];
        }

        var action = rest.Trim().Trim('/').ToLowerInvariant();
        if (action.Length == 0) return false;
        if (!IsKnownAction(action)) return false;

        double? volume = null;
        if (action == "volume")
        {
            if (!TryGetQueryValue(query, "value", out var raw)) return false;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return false;
            volume = Math.Clamp(value, 0d, 100d);
        }

        command = new UriCommand(action, volume);
        return true;
    }

    private static bool IsKnownAction(string action) => action switch
    {
        "play" or "pause" or "toggle" or "next" or "previous" or "mute" or "show" or "volume" => true,
        _ => false,
    };

    private static bool TryGetQueryValue(string? query, string key, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(query)) return false;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            if (!string.Equals(pair[..idx], key, StringComparison.OrdinalIgnoreCase)) continue;
            value = Uri.UnescapeDataString(pair[(idx + 1)..]);
            return true;
        }
        return false;
    }

    private bool IsExpired(PendingCommand pending) =>
        DateTime.UtcNow - pending.EnqueuedAtUtc > CommandTimeout;

    private void SweepExpired()
    {
        // 定时器回调运行在线程池线程：此处若有异常抛出且无人接管，在 .NET 上会导致进程崩溃。
        // 故整体兜底捕获，仅记日志（清理失败不影响后续重试）。
        try
        {
            if (_backendReady) return;
            if (Volatile.Read(ref _disposed) == 1) return;

            var now = DateTime.UtcNow;
            List<PendingCommand>? expired = null;

            lock (_gate)
            {
                var node = _queue.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (now - node.Value.EnqueuedAtUtc > CommandTimeout)
                    {
                        (expired ??= new List<PendingCommand>()).Add(node.Value);
                        _queue.Remove(node);
                    }
                    node = next;
                }
            }

            if (expired == null) return;
            foreach (var item in expired)
                Logger.Warn($"URI 命令超时作废（后端 {CommandTimeout.TotalSeconds:F0} 秒内未就绪）: {item.Command.Action}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"URI 命令超时清理异常: {ex.Message}");
        }
    }

    private async Task ExecuteAsync(UriCommand command)
    {
        await _execGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var ok = await _dispatcher
                .ExecuteAsync(command.Action, command.Volume, CancellationToken.None)
                .ConfigureAwait(false);
            if (ok) Logger.Info($"URI 命令已执行: {command.Action}");
            else Logger.Warn($"URI 命令执行失败: {command.Action}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"URI 命令执行异常: {ex.Message}");
        }
        finally
        {
            _execGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _sweepTimer.Dispose(); } catch { /* 忽略 */ }
        lock (_gate) _queue.Clear();
    }
}
