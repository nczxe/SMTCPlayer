using System.Net.Sockets;
using System.Text;

namespace SMTCPlayer.Core.LanProtocol;

/// <summary>
/// 单条 TCP 连接的读写与状态封装。写入经信号量串行化（指令结果与状态推送可能并发），
/// 发送超时/取消由调用方通过 <see cref="SendAsync"/> 的令牌控制。
/// </summary>
internal sealed class LanProtocolConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    public LanProtocolConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        Id = Guid.NewGuid();
        RemoteEndPoint = client.Client?.RemoteEndPoint?.ToString() ?? "<unknown>";
        LastActivityTicks = Environment.TickCount64;
    }

    public Guid Id { get; }

    public string RemoteEndPoint { get; }

    public Stream Stream => _stream;

    public bool IsAuthenticated { get; set; }

    public int FailedAuthAttempts { get; set; }

    private long LastActivityTicks { get; set; }

    /// <summary>距今未收到任何消息的毫秒数。</summary>
    public long IdleMilliseconds => unchecked(Environment.TickCount64 - LastActivityTicks);

    /// <summary>记录一次入站活动（用于空闲超时判定）。</summary>
    public void MarkActivity() => LastActivityTicks = Environment.TickCount64;

    /// <summary>写入一行（自动追加换行符）。同一连接上的写入按调用顺序串行执行。</summary>
    public async Task SendAsync(string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>强制关闭底层套接字，使阻塞中的读取/写入立即返回。可重复调用。</summary>
    public void Abort()
    {
        try { _client.Close(); }
        catch { /* 关闭失败忽略 */ }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return ValueTask.CompletedTask;

        try { _client.Close(); }
        catch { /* 忽略 */ }
        try { _stream.Dispose(); }
        catch { /* 忽略 */ }
        return ValueTask.CompletedTask;
    }
}
