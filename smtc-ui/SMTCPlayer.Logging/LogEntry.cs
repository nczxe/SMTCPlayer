namespace SMTCPlayer.Logging;

/// <summary>
/// 单条日志记录。异常信息已拼接进 <see cref="Message"/>（多行）。
/// </summary>
public sealed class LogEntry
{
    /// <summary>记录时间（本地时间）。</summary>
    public DateTime Timestamp { get; }

    /// <summary>日志级别。</summary>
    public LogLevel Level { get; }

    /// <summary>分类名（如 "Core"、"WinUI"、"Plugin:netease-enhance"）。</summary>
    public string Category { get; }

    /// <summary>日志消息（异常已拼接）。</summary>
    public string Message { get; }

    /// <summary>发布侧分配的单调序号（写线程按缺口统计拥塞丢弃数）。</summary>
    internal long Sequence { get; }

    internal LogEntry(DateTime timestamp, LogLevel level, string category, string message, long sequence)
    {
        Timestamp = timestamp;
        Level = level;
        Category = category;
        Message = message;
        Sequence = sequence;
    }
}
