using SMTCPlayer.Core.Services;
using SMTCPlayer.PluginApi;

namespace SMTCPlayer.Core.Plugins;

/// <summary>
/// IPluginContext 的宿主实现：把插件对上下文的调用收敛为
/// 快照读取、API 转发、带前缀日志与隔离设置四类受控行为。
/// </summary>
internal sealed class PluginContextImpl : IPluginContext
{
    private readonly PluginHost _host;
    private readonly string _pluginId;
    private readonly SmtcApiClient _api;

    public MediaSnapshot Current => _host.CurrentSnapshot;
    public string ServerUrl => _api.BaseUrl;
    public IPlaybackController Playback { get; }
    public IPluginLogger Log { get; }
    public IPluginSettings Settings { get; }

    public PluginContextImpl(PluginHost host, string pluginId, SmtcApiClient api)
    {
        _host = host;
        _pluginId = pluginId;
        _api = api;
        Playback = new PlaybackControllerImpl(api);
        Log = new PluginLoggerImpl(pluginId);
        Settings = new PluginSettingsStore(pluginId);
    }

    /// <summary>播放控制实现：全部转发到 SmtcApiClient，不触碰宿主内部状态。</summary>
    private sealed class PlaybackControllerImpl : IPlaybackController
    {
        private readonly SmtcApiClient _api;

        public PlaybackControllerImpl(SmtcApiClient api) => _api = api;

        public Task<bool> PlayAsync() => _api.SendControlAsync("play");
        public Task<bool> PauseAsync() => _api.SendControlAsync("pause");
        public Task<bool> TogglePlayPauseAsync() => _api.SendControlAsync("play_pause");
        public Task<bool> NextAsync() => _api.SendControlAsync("next");
        public Task<bool> PreviousAsync() => _api.SendControlAsync("previous");
        public Task<bool> ToggleMuteAsync() => _api.ToggleMuteAsync();

        public Task<bool> SetVolumeAsync(int volume) =>
            _api.SetVolumeAsync(Math.Clamp(volume, 0, 100));
    }

    /// <summary>日志实现：所有输出自动带插件前缀，走宿主统一日志。</summary>
    private sealed class PluginLoggerImpl : IPluginLogger
    {
        private readonly string _prefix;

        public PluginLoggerImpl(string pluginId) => _prefix = $"[插件:{pluginId}] ";

        public void Debug(string message) => Logger.Debug(_prefix + message);
        public void Info(string message) => Logger.Info(_prefix + message);
        public void Warn(string message) => Logger.Warn(_prefix + message);
        public void Error(string message) => Logger.Error(_prefix + message);
        public void Error(string message, Exception exception) => Logger.Error(_prefix + message, exception);
    }
}
