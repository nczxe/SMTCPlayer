namespace SMTCPlayer.Logging;

/// <summary>
/// 定容环形缓冲：保留最近 N 条已受理日志，供日志查看器快照读取。线程安全。
/// </summary>
internal sealed class RingBuffer
{
    private readonly LogEntry[] _items;
    private readonly object _lock = new();
    private int _head;
    private int _count;

    public RingBuffer(int capacity) => _items = new LogEntry[capacity];

    public void Add(LogEntry entry)
    {
        lock (_lock)
        {
            _items[_head] = entry;
            _head = (_head + 1) % _items.Length;
            if (_count < _items.Length) _count++;
        }
    }

    /// <summary>按时间顺序（旧→新）复制当前缓冲内容。</summary>
    public LogEntry[] Snapshot()
    {
        lock (_lock)
        {
            var result = new LogEntry[_count];
            var start = (_head - _count + _items.Length) % _items.Length;
            for (var i = 0; i < _count; i++)
                result[i] = _items[(start + i) % _items.Length];
            return result;
        }
    }
}
