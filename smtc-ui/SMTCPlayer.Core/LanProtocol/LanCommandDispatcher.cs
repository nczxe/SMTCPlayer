using SMTCPlayer.Core.Services;

namespace SMTCPlayer.Core.LanProtocol;

/// <summary>
/// 把 LAN 指令映射为后端 API 调用。<c>volume</c> 取 0~100（与宿主音量语义一致），
/// <c>seek</c> 因后端未提供端点（见 server/static/player.js 注释）固定返回不支持。
/// 内部异常一律吞掉并返回 false，不向客户端泄露细节。
/// </summary>
internal sealed class LanCommandDispatcher
{
    private readonly SmtcApiClient _api;

    public LanCommandDispatcher(SmtcApiClient api) => _api = api;

    public async Task<bool> ExecuteAsync(string? action, double? value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(action))
            return false;

        try
        {
            return action.Trim().ToLowerInvariant() switch
            {
                "play" => await _api.SendControlAsync("play").ConfigureAwait(false),
                "pause" => await _api.SendControlAsync("pause").ConfigureAwait(false),
                "toggle" => await _api.SendControlAsync("play_pause").ConfigureAwait(false),
                "next" => await _api.SendControlAsync("next").ConfigureAwait(false),
                "previous" => await _api.SendControlAsync("previous").ConfigureAwait(false),
                "mute" => await _api.ToggleMuteAsync().ConfigureAwait(false),
                "volume" => value is null
                    ? false
                    : await _api.SetVolumeAsync(Math.Clamp(value.Value, 0d, 100d)).ConfigureAwait(false),
                "seek" => false,
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }
}
