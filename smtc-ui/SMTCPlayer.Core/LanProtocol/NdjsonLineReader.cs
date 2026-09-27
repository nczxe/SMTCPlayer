using System.Text;

namespace SMTCPlayer.Core.LanProtocol;

/// <summary>单条 NDJSON 消息超过配置上限时抛出，由连接层捕获并断开。</summary>
internal sealed class LanMessageTooLargeException : Exception
{
    public LanMessageTooLargeException(string message) : base(message) { }
}

/// <summary>
/// 逐行读取 NDJSON 的缓冲读取器：自行扫描 <c>\n</c> 分隔符，
/// 在把整行读入内存之前即按字节数施加上限，避免超长行导致内存膨胀。
/// 不拥有底层流（不负责释放）。
/// </summary>
internal sealed class NdjsonLineReader
{
    private readonly Stream _stream;
    private readonly int _maxLineBytes;
    private byte[] _buffer;
    private int _start;
    private int _end;

    public NdjsonLineReader(Stream stream, int maxLineBytes)
    {
        _stream = stream;
        _maxLineBytes = Math.Max(1024, maxLineBytes);
        _buffer = new byte[8192];
    }

    /// <summary>
    /// 读取下一行（不含换行符，自动去除行尾 <c>\r</c>）。
    /// 返回 null 表示对端已关闭（EOF）；超过上限抛 <see cref="LanMessageTooLargeException"/>。
    /// </summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var lineLength = newline - _start;
                if (lineLength > _maxLineBytes)
                    throw new LanMessageTooLargeException($"单条消息超过 {_maxLineBytes} 字节上限");

                var contentEnd = lineLength > 0 && _buffer[newline - 1] == (byte)'\r' ? newline - 1 : newline;
                var line = Encoding.UTF8.GetString(_buffer, _start, contentEnd - _start);
                _start = newline + 1;
                if (_start >= _end) { _start = 0; _end = 0; }
                return line;
            }

            // 尚无换行符：已达上限即判超限（严格大于，允许恰好等于上限的行）
            if (_end - _start > _maxLineBytes)
                throw new LanMessageTooLargeException($"单条消息超过 {_maxLineBytes} 字节上限");

            // 回收已消费的前缀，必要时扩容
            if (_start > 0 && (_end == _buffer.Length || _start >= _buffer.Length / 2))
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                var desired = _buffer.Length * 2;
                if (desired <= _buffer.Length) desired = _buffer.Length + 4096;
                Array.Resize(ref _buffer, desired);
            }

            var read = await _stream.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                // EOF：把残留的不完整行（无换行结尾）作为最后一行返回
                if (_end > _start)
                {
                    var tail = Encoding.UTF8.GetString(_buffer, _start, _end - _start);
                    _start = 0; _end = 0;
                    return tail;
                }
                return null;
            }
            _end += read;
        }
    }
}
