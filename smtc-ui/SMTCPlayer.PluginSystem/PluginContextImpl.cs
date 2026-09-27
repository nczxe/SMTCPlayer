using SMTCPlayer.Logging;
using SMTCPlayer.PluginApi;

namespace SMTCPlayer.PluginSystem;

/// <summary>
/// IPluginContext 的宿主实现：把插件对上下文的调用收敛为
/// 快照读取、桥接转发、独立分类日志与隔离设置四类受控行为。
/// </summary>
internal sealed class PluginContextImpl : IPluginContext
{
    private readonly PluginManager _host;
    private readonly string _pluginId;
    private readonly BackendBridgeClient _bridge;

    public MediaSnapshot Current => _host.CurrentSnapshot;
    public string ServerUrl => _bridge.BaseUrl;
    public IPlaybackController Playback { get; }
    public IPluginLogger Log { get; }
    public IPluginSettings Settings { get; }

    public PluginContextImpl(PluginManager host, string pluginId, BackendBridgeClient bridge)
    {
        _host = host;
        _pluginId = pluginId;
        _bridge = bridge;
        Playback = new PlaybackControllerImpl(bridge);
        Log = new PluginLoggerImpl(pluginId);
        Settings = new PluginSettingsStore(pluginId);
    }

    /// <summary>播放控制实现：全部转发到 BackendBridgeClient，不触碰宿主内部状态。</summary>
    private sealed class PlaybackControllerImpl : IPlaybackController
    {
        private readonly BackendBridgeClient _bridge;

        public PlaybackControllerImpl(BackendBridgeClient bridge) => _bridge = bridge;

        public Task<bool> PlayAsync() => _bridge.SendControlAsync("play");
        public Task<bool> PauseAsync() => _bridge.SendControlAsync("pause");
        public Task<bool> TogglePlayPauseAsync() => _bridge.SendControlAsync("play_pause");
        public Task<bool> NextAsync() => _bridge.SendControlAsync("next");
        public Task<bool> PreviousAsync() => _bridge.SendControlAsync("previous");
        public Task<bool> ToggleMuteAsync() => _bridge.ToggleMuteAsync();

        public Task<bool> SetVolumeAsync(int volume) =>
            _bridge.SetVolumeAsync(Math.Clamp(volume, 0, 100));
    }

    /// <summary>
    /// 日志实现：每个插件独立分类（Plugin:&lt;id&gt;）。
    /// 日志引擎对该前缀内置"独立插件文件 + Warn/Error 镜像主日志"路由，
    /// 分类即插件标识，无需再手动拼接前缀。
    /// </summary>
    private sealed class PluginLoggerImpl : IPluginLogger
    {
        private readonly ILog _log;

        public PluginLoggerImpl(string pluginId) =>
            _log = LogManager.GetLogger($"Plugin:{pluginId}");

        public void Debug(string message) => _log.Debug(message);
        public void Info(string message) => _log.Info(message);
        public void Warn(string message) => _log.Warn(message);
        public void Error(string message) => _log.Error(message);
        public void Error(string message, Exception exception) => _log.Error(message, exception);
    }
}
