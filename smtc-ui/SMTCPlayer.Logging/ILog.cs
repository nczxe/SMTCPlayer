namespace SMTCPlayer.Logging;

/// <summary>
/// 分类日志实例。所有方法线程安全且永不抛异常；
/// 引擎未初始化时调用安全降级（仅调试输出，不落盘）。
/// </summary>
public interface ILog
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Error(string message, Exception exception);
}
