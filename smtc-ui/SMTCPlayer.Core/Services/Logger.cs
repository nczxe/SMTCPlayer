using System.Diagnostics;
using System.Text;

namespace SMTCPlayer.Core.Services;

/// <summary>
/// 轻量级文件日志器。线程安全，支持级别过滤、文件轮转与旧日志自动清理。
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static string _logDir = "";

    /// <summary>单日志文件最大大小（字节），超出后自动轮转到带时间戳的归档文件。</summary>
    private const long MaxFileSize = 5 * 1024 * 1024; // 5 MB

    /// <summary>超过此天数的日志文件会在 Init 时自动清理。</summary>
    private const int RetentionDays = 7;

    /// <summary>当前日志级别阈值。低于此级别的日志会被丢弃。</summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Debug;

    public static string LogFilePath { get; private set; } = "";

    public static void Init(string? logDir = null)
    {
        _logDir = logDir ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(_logDir);

        // 读取级别配置（环境变量 SMTC_LOG_LEVEL = DEBUG|INFO|WARN|ERROR）
        var envLevel = Environment.GetEnvironmentVariable("SMTC_LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(envLevel) && Enum.TryParse<LogLevel>(envLevel, true, out var parsed))
        {
            MinLevel = parsed;
        }

        LogFilePath = Path.Combine(_logDir, $"smtc-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        // 清理过期日志
        CleanupOldLogs();

        Info("Logger 初始化完成");
        Info($"日志文件: {LogFilePath}");
        Info($"日志级别: {MinLevel}");
        Info($"工作目录: {AppDomain.CurrentDomain.BaseDirectory}");
        Info($"运行平台: {Environment.OSVersion}");
        Info($".NET 版本: {Environment.Version}");
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message}\n{ex}");

    private static void Write(LogLevel level, string message)
    {
        if (level < MinLevel) return;

        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level,-5}] {message}";

        // 注意：这里必须用完全限定名 System.Diagnostics.Debug，避免与本类新增的 Logger.Debug 方法歧义
        System.Diagnostics.Debug.WriteLine(line);

        try
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(LogFilePath)) return;
                File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
                RotateIfNeeded();
            }
        }
        catch { /* 日志写入失败不应影响主流程 */ }

        try
        {
            DebugWindowSink?.Invoke($"[{level}] {message}");
        }
        catch { }
    }

    /// <summary>
    /// 当当前日志文件超过 <see cref="MaxFileSize"/> 时，将其重命名为带后缀的归档文件，
    /// 然后重置 <see cref="LogFilePath"/> 继续写入新文件。
    /// </summary>
    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(LogFilePath)) return;
            var info = new FileInfo(LogFilePath);
            if (info.Length < MaxFileSize) return;

            var dir = Path.GetDirectoryName(LogFilePath) ?? _logDir;
            var name = Path.GetFileNameWithoutExtension(LogFilePath);
            var archive = Path.Combine(dir, $"{name}-rotated-{DateTime.Now:HHmmss}.log");
            File.Move(LogFilePath, archive);
            // 下一条 Write 会自动创建新文件（File.AppendAllText 行为）
            Info($"日志已轮转，归档为: {Path.GetFileName(archive)}");
        }
        catch { /* 轮转失败不影响写入 */ }
    }

    /// <summary>删除超过 <see cref="RetentionDays"/> 天的 .log 文件。</summary>
    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(_logDir, "*.log"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < cutoff)
                {
                    try { info.Delete(); } catch { }
                }
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }

    public static Action<string>? DebugWindowSink { get; set; }
}

/// <summary>日志级别（数值越大，优先级越高）。</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}
