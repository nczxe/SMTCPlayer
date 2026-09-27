using System.Diagnostics;
using System.Runtime.CompilerServices;
using SMTCPlayer.Logging;

namespace SMTCPlayer.Core.Services;

/// <summary>
/// 轻量级文件日志器的兼容门面：公共 API 保持不变，
/// 实现转发到 SMTCPlayer.Logging 引擎（后台批量写盘、按日分卷、插件路由、量控）。
/// 线程安全，支持级别过滤、分类覆盖与旧日志自动清理；
/// 按调用方程序集自动归类（Core / WinUI / Wpf / App）。
/// </summary>
public static class Logger
{
    private static Action<string>? _debugWindowSink;
    private static Action<LogEntry>? _sinkBridge;

    /// <summary>当前日志级别阈值。低于此级别的日志会被丢弃。</summary>
    public static LogLevel MinLevel
    {
        get => (LogLevel)LogManager.MinLevel;
        set => LogManager.MinLevel = (Logging.LogLevel)value;
    }

    /// <summary>当前主日志文件路径（由引擎决定，默认位于 %LocalAppData%\SMTCPlayer\logs）。</summary>
    public static string LogFilePath => LogManager.LogFilePath;

    /// <summary>
    /// 初始化日志引擎。读取级别配置（环境变量 SMTC_LOG_LEVEL = DEBUG|INFO|WARN|ERROR）、
    /// 清理过期日志、启动后台写线程并写入启动横幅。幂等，重复调用安全。
    /// </summary>
    public static void Init(string? logDir = null) => LogManager.Init(logDir);

    /// <summary>
    /// 调试窗口订阅回调：每条已受理日志触发一次，格式 [LEVEL] message。
    /// 重复赋值安全（旧桥接自动解绑）。
    /// </summary>
    public static Action<string>? DebugWindowSink
    {
        get => _debugWindowSink;
        set
        {
            if (_sinkBridge != null)
                LogManager.EntryLogged -= _sinkBridge;
            _debugWindowSink = value;
            if (value == null)
            {
                _sinkBridge = null;
                return;
            }
            _sinkBridge = entry => value($"[{entry.Level}] {entry.Message}");
            LogManager.EntryLogged += _sinkBridge;
        }
    }

    // 注意：NoInlining 保证栈帧结构稳定，使 ResolveCallerCategory 的帧偏移可靠；
    // 方法内必须用完全限定名 System.Diagnostics.Debug，避免与本类 Debug 方法歧义

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Debug(string message) => Write(LogLevel.Debug, message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Info(string message) => Write(LogLevel.Info, message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Warn(string message) => Write(LogLevel.Warn, message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Error(string message) => Write(LogLevel.Error, message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Error(string message, Exception ex) =>
        Write(LogLevel.Error, $"{message}\n{ex}");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Write(LogLevel level, string message)
    {
        var log = LogManager.GetLogger(ResolveCallerCategory());
        switch (level)
        {
            case LogLevel.Debug: log.Debug(message); break;
            case LogLevel.Info: log.Info(message); break;
            case LogLevel.Warn: log.Warn(message); break;
            case LogLevel.Error: log.Error(message); break;
        }
    }

    /// <summary>
    /// 按调用方程序集归类分类。栈帧采样开销为微秒级，现有调用频率下可接受。
    /// </summary>
    private static string ResolveCallerCategory()
    {
        try
        {
            // 帧 0=ResolveCallerCategory, 1=Write, 2=Logger.Debug/Info/..., 3=业务调用方
            var assemblyName = new StackFrame(3).GetMethod()?.DeclaringType?.Assembly.GetName().Name;
            return assemblyName switch
            {
                "SMTCPlayer.Core" => "Core",
                "SMTCPlayer.WinUI" => "WinUI",
                "SMTCPlayer.Wpf" => "Wpf",
                _ => "App",
            };
        }
        catch
        {
            return "App";
        }
    }
}

/// <summary>日志级别（数值越大，优先级越高；与 SMTCPlayer.Logging.LogLevel 数值一致）。</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}
