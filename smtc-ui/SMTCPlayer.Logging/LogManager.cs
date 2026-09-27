using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace SMTCPlayer.Logging;

/// <summary>
/// 轻量级结构化日志引擎：分类日志、后台线程批量写盘、主日志按日分卷、
/// 插件日志路由、重复抑制与 Debug 限流、过期清理。
/// 全组件线程安全，写日志永不抛异常；未初始化时调用安全（仅调试输出）。
/// </summary>
public static class LogManager
{
    private const int ChannelCapacity = 4096;
    private const int RingCapacity = 2000;
    private const string PluginPrefix = "Plugin:";

    /// <summary>主日志单文件分卷阈值（2MB）。internal 供测试注入。</summary>
    internal static long MainFileMaxBytes = 2L * 1024 * 1024;

    /// <summary>插件日志单文件分卷阈值（1MB）。internal 供测试注入。</summary>
    internal static long PluginFileMaxBytes = 1L * 1024 * 1024;

    private static readonly Channel<LogEntry> _channel = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // 拥塞丢最老，由写线程按序号缺口统计
            SingleReader = true,
        });

    private static readonly RingBuffer _ring = new(RingCapacity);
    private static readonly ConcurrentDictionary<string, ILog> _loggers = new();
    private static readonly ConcurrentDictionary<string, LogLevel> _categoryLevels = new();
    private static readonly LogThrottle _throttle = new();

    private static readonly object _initLock = new();
    private static volatile bool _initialized;
    private static volatile bool _running;
    private static InternalLogWriter? _writer;
    private static Thread? _writerThread;
    private static int _minLevel = (int)LogLevel.Debug;
    private static long _sequence;

    static LogManager()
    {
        // 进程退出兜底：即使宿主忘记调用 FlushAndWait，也限时冲刷一次
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { FlushAndWait(TimeSpan.FromSeconds(2)); } catch { }
        };
    }

    /// <summary>当前全局最低日志级别（分类覆盖优先）。运行时可改，立即生效。</summary>
    public static LogLevel MinLevel
    {
        get => (LogLevel)Volatile.Read(ref _minLevel);
        set => Volatile.Write(ref _minLevel, (int)value);
    }

    /// <summary>当前主日志文件路径；未初始化时为空字符串。</summary>
    public static string LogFilePath => _writer?.MainLogPath ?? "";

    /// <summary>每条已受理日志触发一次（发布线程同步回调，订阅方异常被吞）。</summary>
    public static event Action<LogEntry>? EntryLogged;

    /// <summary>
    /// 初始化引擎：默认目录 %LocalAppData%\SMTCPlayer\logs。
    /// 读取 SMTC_LOG_LEVEL 环境变量（DEBUG|INFO|WARN|ERROR）设置全局级别、
    /// 清理过期日志、启动后台写线程并写入启动横幅。幂等，重复调用安全。
    /// </summary>
    public static void Init(string? logDir = null)
    {
        lock (_initLock)
        {
            if (_initialized) return;

            var envLevel = Environment.GetEnvironmentVariable("SMTC_LOG_LEVEL");
            if (!string.IsNullOrWhiteSpace(envLevel) && Enum.TryParse<LogLevel>(envLevel, true, out var parsed))
                MinLevel = parsed;

            var dir = logDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SMTCPlayer", "logs");

            var writer = new InternalLogWriter(dir);
            writer.CleanupExpired();
            _writer = writer;
            _running = true;
            _initialized = true;

            // 后台线程：IsBackground=true 保证进程退出不被挂死；
            // 正常退出由 FlushAndWait 显式冲刷，ProcessExit 兜底
            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "smtc-log-writer",
            };
            _writerThread.Start();

            // 启动横幅（等价旧 Logger.Init 的启动信息）
            Publish("Logging", LogLevel.Info, "日志引擎初始化完成");
            Publish("Logging", LogLevel.Info, $"日志文件: {writer.MainLogPath}");
            Publish("Logging", LogLevel.Info, $"日志级别: {MinLevel}");
            Publish("Logging", LogLevel.Info, $"日志目录: {dir}");
            Publish("Logging", LogLevel.Info, $"运行平台: {Environment.OSVersion}");
            Publish("Logging", LogLevel.Info, $".NET 版本: {Environment.Version}");
        }
    }

    /// <summary>
    /// 获取分类日志实例（如 "Core"、"WinUI"、"Plugin:netease-enhance"）。
    /// 未初始化时调用安全（实例写日志自动降级）。
    /// </summary>
    public static ILog GetLogger(string category) =>
        _loggers.GetOrAdd(string.IsNullOrEmpty(category) ? "App" : category, static c => new CategoryLog(c));

    /// <summary>为指定分类设置级别覆盖（优先于全局 MinLevel；插件分类可用此收紧为全量之外的级别）。</summary>
    public static void SetCategoryLevel(string category, LogLevel level) => _categoryLevels[category] = level;

    /// <summary>读取分类级别覆盖；未设置时返回 null。</summary>
    public static LogLevel? GetCategoryLevel(string category) =>
        _categoryLevels.TryGetValue(category, out var level) ? level : null;

    /// <summary>内存环形缓冲快照：最近 2000 条已受理日志（旧→新）。</summary>
    public static IReadOnlyList<LogEntry> SnapshotEntries() => _ring.Snapshot();

    /// <summary>退出前限时冲刷：停止接收并等待后台线程把剩余队列写盘。</summary>
    public static void FlushAndWait(TimeSpan timeout)
    {
        var thread = _writerThread;
        if (thread == null) return;
        _running = false;
        try
        {
            if (thread.IsAlive) thread.Join(timeout);
        }
        catch { }
    }

    // ===== 发布管线 =====

    /// <summary>
    /// 日志发布入口：级别过滤（分类覆盖 &gt; 插件全量 &gt; 全局 MinLevel）→
    /// 量控（Debug 限流 + 重复抑制）→ 环形缓冲 / 事件 / 调试输出 / 有界通道。
    /// </summary>
    internal static void Publish(string category, LogLevel level, string message)
    {
        try
        {
            if (!_initialized)
            {
                // 初始化前（或初始化失败时）安全降级：仅调试输出，不落盘不抛异常
                Debug.WriteLine(FormatLine(DateTime.Now, level, category, message));
                return;
            }

            if (level < EffectiveLevel(category)) return;

            var (accepted, summaries) = _throttle.TryAccept(category, level, message, Environment.TickCount64);
            if (summaries != null)
                foreach (var summary in summaries)
                    Enqueue(summary);
            if (!accepted) return;

            Enqueue(new LogEntry(DateTime.Now, level, category, message, NextSequence()));
        }
        catch { /* 写日志永不抛异常 */ }
    }

    internal static long NextSequence() => Interlocked.Increment(ref _sequence);

    internal static bool IsPluginCategory(string category) =>
        category.StartsWith(PluginPrefix, StringComparison.Ordinal);

    internal static string FormatLine(DateTime timestamp, LogLevel level, string category, string message) =>
        $"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {message}";

    private static void Enqueue(LogEntry entry)
    {
        _ring.Add(entry);
        Debug.WriteLine(FormatLine(entry.Timestamp, entry.Level, entry.Category, entry.Message));
        RaiseEntryLogged(entry);
        _channel.Writer.TryWrite(entry);
    }

    private static void RaiseEntryLogged(LogEntry entry)
    {
        var handlers = EntryLogged;
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<LogEntry>>())
        {
            try { handler(entry); } catch { }
        }
    }

    private static LogLevel EffectiveLevel(string category)
    {
        if (_categoryLevels.TryGetValue(category, out var level)) return level;
        if (IsPluginCategory(category)) return LogLevel.Debug; // 插件文件全量级别（告警才镜像主日志）
        return MinLevel;
    }

    // ===== 后台写线程 =====

    private static void WriterLoop()
    {
        var writer = _writer!;
        var batch = new List<LogEntry>(256);
        long lastMaintenance = 0;

        while (_running)
        {
            try
            {
                if (!_channel.Reader.TryRead(out var first))
                {
                    Thread.Sleep(50);
                    continue;
                }

                batch.Add(first);
                // 批量窗口：最多再等 200ms 或攒满 256 条
                var deadline = Environment.TickCount64 + 200;
                while (batch.Count < 256)
                {
                    if (_channel.Reader.TryRead(out var next))
                    {
                        batch.Add(next);
                        continue;
                    }
                    var remain = deadline - Environment.TickCount64;
                    if (remain <= 0) break;
                    Thread.Sleep((int)Math.Min(remain, 20));
                }

                writer.WriteBatch(batch);
                batch.Clear();

                // 每秒一次维护：结算量控汇总 + 每小时清理过期日志
                var now = Environment.TickCount64;
                if (now - lastMaintenance >= 1000)
                {
                    lastMaintenance = now;
                    var summaries = _throttle.DrainExpired(now);
                    if (summaries != null)
                        foreach (var summary in summaries)
                            Enqueue(summary);
                    writer.PeriodicCleanup(now);
                }
            }
            catch { /* 写线程任何异常都不能终止循环 */ }
        }

        // 退出路径：排空剩余队列，限时写盘
        try
        {
            while (_channel.Reader.TryRead(out var rest))
            {
                batch.Add(rest);
                if (batch.Count >= 256)
                {
                    writer.WriteBatch(batch);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                writer.WriteBatch(batch);
                batch.Clear();
            }
            var tail = _throttle.DrainExpired(Environment.TickCount64);
            if (tail != null)
                writer.WriteBatch(tail);
        }
        catch { }
    }
}
