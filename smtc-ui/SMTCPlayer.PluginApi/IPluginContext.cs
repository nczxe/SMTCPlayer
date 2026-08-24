namespace SMTCPlayer.PluginApi;

/// <summary>
/// Core 在插件加载时提供的受控上下文——插件唯一被允许的主动访问面：
/// 只读快照、播放控制、专属日志与隔离设置。
/// 刻意不提供：文件系统、进程管理、任意 HTTP 能力。
/// 需要联网的插件直接自行 new HttpClient()。
/// </summary>
public interface IPluginContext
{
    /// <summary>最近一次轮询得到的媒体快照（只读）。</summary>
    MediaSnapshot Current { get; }

    /// <summary>
    /// 本地后端服务基础地址（如 http://127.0.0.1:8888）。
    /// 仅供插件自行访问本机 API（回环免认证）；不包含认证 token。
    /// </summary>
    string ServerUrl { get; }

    /// <summary>受控播放控制（异步转发到后端 API）。</summary>
    IPlaybackController Playback { get; }

    /// <summary>插件专属日志（自动带插件标识前缀写入宿主统一日志）。</summary>
    IPluginLogger Log { get; }

    /// <summary>插件专属的隔离键值设置存储（每个插件独立文件，互不可见）。</summary>
    IPluginSettings Settings { get; }
}
