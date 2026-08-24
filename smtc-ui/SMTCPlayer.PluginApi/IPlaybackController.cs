namespace SMTCPlayer.PluginApi;

/// <summary>受控播放能力。所有操作异步转发到后端服务，不直接触碰播放器进程。</summary>
public interface IPlaybackController
{
    Task<bool> PlayAsync();

    Task<bool> PauseAsync();

    Task<bool> TogglePlayPauseAsync();

    Task<bool> NextAsync();

    Task<bool> PreviousAsync();

    /// <summary>设置主音量（0-100，超出范围会被截断）。</summary>
    Task<bool> SetVolumeAsync(int volume);

    Task<bool> ToggleMuteAsync();
}
