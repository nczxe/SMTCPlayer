namespace SMTCPlayer.PluginApi;

/// <summary>插件日志（写入宿主统一日志文件，自动带插件标识前缀）。</summary>
public interface IPluginLogger
{
    void Debug(string message);

    void Info(string message);

    void Warn(string message);

    void Error(string message);

    void Error(string message, Exception exception);
}
