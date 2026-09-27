using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SMTCPlayer.Core.Services;
using SMTCPlayer.PluginApi;
using SMTCPlayer.PluginSystem;

namespace SMTCPlayer.Core.LanProtocol;

/// <summary>
/// 局域网通信服务：TCP + 逐行 NDJSON 自有协议。
/// <para>
/// 连接后首条消息必须为 <c>{"type":"auth","pin":...}</c>，服务端经后端 <c>/api/auth/login</c> 校验；
/// 已鉴权连接可发送 指令（play/pause/toggle/next/previous/mute/volume/seek）与 get_status；
/// 服务端订阅插件广播池，媒体状态变化时向所有已鉴权连接推送 <c>{"type":"status",...}</c>；
/// 含 ping/pong 心跳与空闲超时断开。
/// </para>
/// <para>
/// 资源限制：单条消息 64KB（超出断开记 Warn）、最大并发已鉴权连接 16（超出拒绝）、
/// 单连接鉴权尝试 5 次（超限断开）；错误响应不含内部细节；单连接异常仅断开该连接。
/// </para>
/// </summary>
public sealed class LanProtocolServer : IAsyncDisposable
{
    /// <summary>单次发送的最长等待时间，避免慢连接长时间拖住广播分发。</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private readonly LanProtocolOptions _options;
    private readonly SmtcApiClient _api;
    private readonly PluginManager _pluginManager;
    private readonly LanCommandDispatcher _dispatcher;
    private readonly ConcurrentDictionary<Guid, LanProtocolConnection> _connections = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Task? _heartbeatTask;
    private IDisposable? _subscription;
    private int _authenticatedCount;
    private int _running;
    private int _disposed;

    public LanProtocolServer(LanProtocolOptions options, SmtcApiClient api, PluginManager pluginManager)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _pluginManager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
        _dispatcher = new LanCommandDispatcher(_api);
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>启动监听、订阅广播池并开启心跳循环。重复调用安全。</summary>
    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return Task.CompletedTask;

        try
        {
            var address = _options.Scope == LanListenScope.AllInterfaces ? IPAddress.Any : IPAddress.Loopback;
            _listener = new TcpListener(address, _options.Port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();

            _cts = new CancellationTokenSource();
            _subscription = _pluginManager.Broadcast.Subscribe(new LanBroadcastSubscriber(this));

            var token = _cts.Token;
            _acceptTask = Task.Run(() => AcceptLoopAsync(token));
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(token));

            Logger.Info($"局域网协议服务已启动: {(ReferenceEquals(address, IPAddress.Any) ? "0.0.0.0" : "127.0.0.1")}:{_options.Port}");
            return Task.CompletedTask;
        }
        catch
        {
            Interlocked.Exchange(ref _running, 0);
            throw;
        }
    }

    /// <summary>停止服务：退订、关闭监听与所有连接。重复调用安全。</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
            return;

        try { _subscription?.Dispose(); }
        catch { /* 退订失败忽略 */ }
        _subscription = null;

        try { _cts?.Cancel(); }
        catch { /* 忽略 */ }
        try { _listener?.Stop(); }
        catch { /* 忽略 */ }

        foreach (var conn in _connections.Values)
            conn.Abort();

        if (_acceptTask != null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch { /* 忽略 */ }
        }
        if (_heartbeatTask != null)
        {
            try { await _heartbeatTask.ConfigureAwait(false); }
            catch { /* 忽略 */ }
        }

        foreach (var conn in _connections.Values)
        {
            try { await conn.DisposeAsync().ConfigureAwait(false); }
            catch { /* 忽略 */ }
        }
        _connections.Clear();

        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _acceptTask = null;
        _heartbeatTask = null;

        Logger.Info("局域网协议服务已停止");
    }

    // ============== 接受连接 ==============

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;
        if (listener == null)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested) break;
                continue;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested) break;
                Logger.Warn($"局域网协议接受连接异常: {ex.Message}");
                continue;
            }

            _ = HandleConnectionAsync(client, cancellationToken);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        LanProtocolConnection? conn = null;
        try
        {
            try { client.NoDelay = true; }
            catch { /* 非致命 */ }

            conn = new LanProtocolConnection(client);
            _connections[conn.Id] = conn;

            // 已达已鉴权连接上限：接受后立即回错误并关闭
            if (Volatile.Read(ref _authenticatedCount) >= _options.MaxAuthenticatedConnections)
            {
                await TrySendAsync(conn, BuildError("连接数已达上限"), cancellationToken).ConfigureAwait(false);
                return;
            }

            var reader = new NdjsonLineReader(conn.Stream, _options.MaxMessageBytes);
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (LanMessageTooLargeException)
                {
                    Logger.Warn($"局域网消息超过 {_options.MaxMessageBytes} 字节上限，断开连接 {conn.RemoteEndPoint}");
                    break;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { break; } // 连接级 IO 错误：仅断开本连接

                if (line == null) break; // 对端关闭
                conn.MarkActivity();
                if (string.IsNullOrWhiteSpace(line)) continue;

                bool keep;
                try
                {
                    keep = conn.IsAuthenticated
                        ? await HandleAuthenticatedAsync(conn, line, cancellationToken).ConfigureAwait(false)
                        : await HandleAuthAsync(conn, line, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // 单连接解析/处理异常：仅断开该连接，服务不受影响
                    Logger.Warn($"局域网连接消息处理异常: {ex.Message}");
                    break;
                }

                if (!keep) break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"局域网连接处理异常: {ex.Message}");
        }
        finally
        {
            if (conn != null)
            {
                if (conn.IsAuthenticated)
                    Interlocked.Decrement(ref _authenticatedCount);
                _connections.TryRemove(conn.Id, out _);
                try { await conn.DisposeAsync().ConfigureAwait(false); }
                catch { /* 忽略 */ }
            }
        }
    }

    // ============== 鉴权 ==============

    private async Task<bool> HandleAuthAsync(LanProtocolConnection conn, string line, CancellationToken cancellationToken)
    {
        var msg = LanJson.TryParse(line);
        if (msg == null || !string.Equals(msg.Type, "auth", StringComparison.Ordinal))
        {
            // 未鉴权即发送指令/非法消息：拒绝并断开
            conn.FailedAuthAttempts++;
            await TrySendAsync(conn, BuildError("未鉴权"), cancellationToken).ConfigureAwait(false);
            return false;
        }

        conn.FailedAuthAttempts++;

        bool ok = false;
        try
        {
            var resp = await _api.LoginAsync(msg.Pin ?? string.Empty).ConfigureAwait(false);
            ok = resp?.Success == true && resp.Token != null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"局域网鉴权请求后端失败: {ex.Message}");
            ok = false;
        }

        if (!ok)
        {
            Logger.Warn($"局域网鉴权失败（第 {conn.FailedAuthAttempts} 次）来自 {conn.RemoteEndPoint}");
            await TrySendAsync(conn,
                LanJson.Serialize(new LanOutboundMessage { Type = "auth_result", Ok = false, Error = "鉴权失败" }),
                cancellationToken).ConfigureAwait(false);
            return conn.FailedAuthAttempts < _options.MaxAuthAttempts;
        }

        if (Interlocked.Increment(ref _authenticatedCount) > _options.MaxAuthenticatedConnections)
        {
            Interlocked.Decrement(ref _authenticatedCount);
            await TrySendAsync(conn, BuildError("连接数已达上限"), cancellationToken).ConfigureAwait(false);
            return false;
        }

        conn.IsAuthenticated = true;
        conn.MarkActivity();
        Logger.Info($"局域网客户端已鉴权: {conn.RemoteEndPoint}");

        await TrySendAsync(conn,
            LanJson.Serialize(new LanOutboundMessage { Type = "auth_result", Ok = true }),
            cancellationToken).ConfigureAwait(false);
        await TrySendAsync(conn, BuildStatusJson(_pluginManager.CurrentSnapshot), cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ============== 已鉴权消息 ==============

    private async Task<bool> HandleAuthenticatedAsync(LanProtocolConnection conn, string line, CancellationToken cancellationToken)
    {
        var msg = LanJson.TryParse(line);
        if (msg == null)
        {
            await TrySendAsync(conn, BuildError("消息格式错误"), cancellationToken).ConfigureAwait(false);
            return false; // 解析异常：仅断开该连接
        }

        switch (msg.Type)
        {
            case "command":
            {
                var ok = await _dispatcher.ExecuteAsync(msg.Action, msg.Value, cancellationToken).ConfigureAwait(false);
                await TrySendAsync(conn,
                    LanJson.Serialize(new LanOutboundMessage { Type = "command_result", Ok = ok, Action = msg.Action }),
                    cancellationToken).ConfigureAwait(false);
                if (ok)
                    await TrySendAsync(conn, BuildStatusJson(_pluginManager.CurrentSnapshot), cancellationToken).ConfigureAwait(false);
                return true;
            }

            case "get_status":
                await TrySendAsync(conn, BuildStatusJson(_pluginManager.CurrentSnapshot), cancellationToken).ConfigureAwait(false);
                return true;

            case "ping":
                // 客户端主动心跳：回 pong
                await TrySendAsync(conn, LanJson.Serialize(new LanOutboundMessage { Type = "pong" }), cancellationToken).ConfigureAwait(false);
                return true;

            case "pong":
                // 服务端心跳（HeartbeatLoopAsync）发出 ping 后客户端回的 pong：
                // 仅用于刷新活动时间（MarkActivity 已在读取处完成），静默确认，不再回包。
                // 若落入 default 会回 "未知消息类型" 错误，形成 ping/pong 不对称。
                return true;

            default:
                await TrySendAsync(conn, BuildError("未知消息类型"), cancellationToken).ConfigureAwait(false);
                return true;
        }
    }

    // ============== 状态推送 ==============

    /// <summary>广播池回调：媒体事件到达时向所有已鉴权连接推送最新状态。</summary>
    internal async Task OnBroadcastAsync(PluginEvent pluginEvent, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !IsRunning)
            return;
        if (pluginEvent.Type == PluginEventType.ServerStateChanged)
            return; // 仅推送媒体状态变化

        var targets = _connections.Values.Where(c => c.IsAuthenticated).ToArray();
        if (targets.Length == 0)
            return;

        var json = BuildStatusJson(pluginEvent.After ?? _pluginManager.CurrentSnapshot);
        var token = _cts?.Token ?? CancellationToken.None;

        var sends = new Task<bool>[targets.Length];
        for (int i = 0; i < targets.Length; i++)
            sends[i] = TrySendAsync(targets[i], json, token);
        var results = await Task.WhenAll(sends).ConfigureAwait(false);

        for (int i = 0; i < results.Length; i++)
        {
            if (!results[i])
                targets[i].Abort(); // 发送失败/超时：交由该连接读取循环清理
        }
    }

    // ============== 心跳与空闲超时 ==============

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        var interval = _options.HeartbeatInterval > TimeSpan.Zero
            ? _options.HeartbeatInterval
            : TimeSpan.FromSeconds(20);
        var idleMs = (long)Math.Max(5000, _options.IdleTimeout.TotalMilliseconds);
        var pingJson = LanJson.Serialize(new LanOutboundMessage { Type = "ping" });

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

                foreach (var conn in _connections.Values)
                {
                    if (conn.IdleMilliseconds > idleMs)
                    {
                        Logger.Debug($"局域网连接空闲超时，断开 {conn.RemoteEndPoint}");
                        conn.Abort();
                        continue;
                    }

                    if (conn.IsAuthenticated && !await TrySendAsync(conn, pingJson, cancellationToken).ConfigureAwait(false))
                        conn.Abort();
                }
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (Exception ex)
        {
            Logger.Warn($"局域网心跳循环异常: {ex.Message}");
        }
    }

    // ============== 工具 ==============

    private static string BuildError(string message)
        => LanJson.Serialize(new LanOutboundMessage { Type = "error", Error = message });

    private static string BuildStatusJson(MediaSnapshot snapshot)
        => LanJson.Serialize(new LanOutboundMessage { Type = "status", Data = LanStatusPayload.From(snapshot) });

    private static async Task<bool> TrySendAsync(LanProtocolConnection conn, string json, CancellationToken cancellationToken)
    {
        try
        {
            await conn.SendAsync(json, cancellationToken).WaitAsync(SendTimeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await StopAsync().ConfigureAwait(false);
    }
}
