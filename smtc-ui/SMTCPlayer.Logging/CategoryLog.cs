namespace SMTCPlayer.Logging;

/// <summary>分类日志实例：仅携带分类名，全部转发到 LogManager 发布管线。</summary>
internal sealed class CategoryLog : ILog
{
    private readonly string _category;

    public CategoryLog(string category) => _category = category;

    public void Debug(string message) => LogManager.Publish(_category, LogLevel.Debug, message);
    public void Info(string message) => LogManager.Publish(_category, LogLevel.Info, message);
    public void Warn(string message) => LogManager.Publish(_category, LogLevel.Warn, message);
    public void Error(string message) => LogManager.Publish(_category, LogLevel.Error, message);
    public void Error(string message, Exception exception) =>
        LogManager.Publish(_category, LogLevel.Error, $"{message}\n{exception}");
}
