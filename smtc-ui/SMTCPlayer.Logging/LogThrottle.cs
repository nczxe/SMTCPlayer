namespace SMTCPlayer.Logging;

/// <summary>
/// 量控器：重复抑制（相同 (分类,级别,消息) 10 秒窗口内合并计数）与
/// Debug 每分类每分钟限流。所有方法线程安全；
/// 被抑制/限流的条目在窗口结束或下一条到来时结算为汇总条目，
/// 汇总由调用方直接入队（绕过量控），避免汇总自身被再次抑制。
/// </summary>
internal sealed class LogThrottle
{
    private const long DedupWindowMs = 10_000;
    private const int DebugLimitPerMinute = 120;

    /// <summary>去重字典容量上限：防止海量不同消息撑爆内存，超限后新 key 直接放行。</summary>
    private const int MaxTrackedKeys = 1024;

    private readonly object _lock = new();
    private readonly Dictionary<(string Category, int Level, string Message), DedupState> _dedup = new();
    private readonly Dictionary<string, RateState> _debugRate = new();

    private sealed class DedupState
    {
        public long WindowStart;
        public int Count;
    }

    private sealed class RateState
    {
        public long MinuteId;
        public int Accepted;
        public int Dropped;
    }

    /// <summary>
    /// 判定一条日志是否放行；同时可能结算出上一窗口的汇总条目
    /// （重复汇总 "... (重复 N 次)" / 限流汇总 Warn）。
    /// </summary>
    public (bool Accepted, List<LogEntry>? Summaries) TryAccept(string category, LogLevel level, string message, long now)
    {
        List<LogEntry>? summaries = null;
        lock (_lock)
        {
            // 1) Debug 每分类每分钟限流（Info/Warn/Error 不限流）
            if (level == LogLevel.Debug)
            {
                var minuteId = now / 60_000;
                if (!_debugRate.TryGetValue(category, out var rate))
                {
                    rate = new RateState { MinuteId = minuteId };
                    _debugRate[category] = rate;
                }
                else if (rate.MinuteId != minuteId)
                {
                    // 分钟切换：结算上一分钟的限流汇总
                    if (rate.Dropped > 0)
                    {
                        summaries ??= new List<LogEntry>();
                        summaries.Add(MakeRateSummary(category, rate.Dropped));
                    }
                    rate.MinuteId = minuteId;
                    rate.Accepted = 0;
                    rate.Dropped = 0;
                }
                rate.Accepted++;
                if (rate.Accepted > DebugLimitPerMinute)
                {
                    rate.Dropped++;
                    return (false, summaries);
                }
            }

            // 2) 重复抑制：10 秒窗口内相同 (分类,级别,消息) 只放行第一条
            var key = (category, (int)level, message);
            if (_dedup.TryGetValue(key, out var state))
            {
                if (now - state.WindowStart < DedupWindowMs)
                {
                    state.Count++;
                    return (false, summaries);
                }
                // 窗口过期：结算上一窗口的重复汇总，开启新窗口
                if (state.Count > 0)
                {
                    summaries ??= new List<LogEntry>();
                    summaries.Add(MakeDedupSummary(category, level, message, state.Count));
                }
                state.WindowStart = now;
                state.Count = 0;
                return (true, summaries);
            }

            if (_dedup.Count >= MaxTrackedKeys)
                EvictOneLocked(now); // 满时淘汰最老窗口，保证新 key 总能被跟踪
            _dedup[key] = new DedupState { WindowStart = now };
            return (true, summaries);
        }
    }

    /// <summary>
    /// 去重字典容量满时淘汰一个条目：优先移除已过期窗口，
    /// 否则移除窗口起点最早（最久未刷新）的 key。其未结算的重复计数随之丢弃。
    /// </summary>
    private void EvictOneLocked(long now)
    {
        (string Category, int Level, string Message)? oldest = null;
        long oldestStart = long.MaxValue;
        foreach (var pair in _dedup)
        {
            if (now - pair.Value.WindowStart >= DedupWindowMs)
            {
                _dedup.Remove(pair.Key);
                return;
            }
            if (pair.Value.WindowStart < oldestStart)
            {
                oldestStart = pair.Value.WindowStart;
                oldest = pair.Key;
            }
        }
        if (oldest != null)
            _dedup.Remove(oldest.Value);
    }

    /// <summary>
    /// 结算所有过期窗口，返回应输出的汇总条目（写线程每秒周期调用，
    /// 兜底"窗口结束后不再有同 key 日志"导致汇总丢失的场景）。
    /// </summary>
    public List<LogEntry>? DrainExpired(long now)
    {
        List<LogEntry>? summaries = null;
        lock (_lock)
        {
            if (_dedup.Count > 0)
            {
                List<(string Category, int Level, string Message)>? expired = null;
                foreach (var pair in _dedup)
                {
                    var state = pair.Value;
                    if (now - state.WindowStart < DedupWindowMs) continue;
                    if (state.Count > 0)
                    {
                        summaries ??= new List<LogEntry>();
                        summaries.Add(MakeDedupSummary(pair.Key.Category, (LogLevel)pair.Key.Level, pair.Key.Message, state.Count));
                    }
                    (expired ??= new List<(string, int, string)>()).Add(pair.Key);
                }
                if (expired != null)
                    foreach (var k in expired)
                        _dedup.Remove(k);
            }

            if (_debugRate.Count > 0)
            {
                var minuteId = now / 60_000;
                List<string>? expiredRate = null;
                foreach (var pair in _debugRate)
                {
                    if (pair.Value.MinuteId == minuteId) continue;
                    if (pair.Value.Dropped > 0)
                    {
                        summaries ??= new List<LogEntry>();
                        summaries.Add(MakeRateSummary(pair.Key, pair.Value.Dropped));
                    }
                    (expiredRate ??= new List<string>()).Add(pair.Key);
                }
                if (expiredRate != null)
                    foreach (var c in expiredRate)
                        _debugRate.Remove(c);
            }
        }
        return summaries;
    }

    private static LogEntry MakeDedupSummary(string category, LogLevel level, string message, int count) =>
        new(DateTime.Now, level, category, $"{message} (重复 {count} 次)", LogManager.NextSequence());

    private static LogEntry MakeRateSummary(string category, int dropped) =>
        new(DateTime.Now, LogLevel.Warn, category, $"分类 {category} 的 Debug 日志已限流丢弃 {dropped} 条", LogManager.NextSequence());
}
