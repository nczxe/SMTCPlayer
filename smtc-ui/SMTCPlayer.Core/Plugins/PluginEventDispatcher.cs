using SMTCPlayer.Core.Models;
using SMTCPlayer.PluginApi;

namespace SMTCPlayer.Core.Plugins;

/// <summary>
/// 事件派发核心：把轮询得到的 PlayerStatus 投影为插件域快照，
/// 并对相邻两次快照做 diff，生成要广播的事件列表。
/// </summary>
internal static class PluginEventDispatcher
{
    /// <summary>PlayerStatus → 插件域只读快照。</summary>
    public static MediaSnapshot ToSnapshot(PlayerStatus s) => new()
    {
        Title = s.Title ?? "",
        Artist = s.Artist ?? "",
        AlbumTitle = s.AlbumTitle ?? "",
        Status = s.Status ?? "stopped",
        Source = string.IsNullOrWhiteSpace(s.Source) ? null : s.Source,
        IsPlaying = s.IsPlaying,
        HasPrevious = s.HasPrevious,
        HasNext = s.HasNext,
        Position = s.Position,
        Duration = s.Duration,
        Volume = s.Volume,
        Muted = s.Muted,
        SongId = s.SongId,
    };

    /// <summary>是否已有有效媒体数据（用于首次快照判断）。</summary>
    public static bool HasMedia(MediaSnapshot s) =>
        !string.IsNullOrEmpty(s.Title) || !string.IsNullOrEmpty(s.Artist);

    /// <summary>
    /// 比较前后快照生成事件列表。
    /// before 为 null 表示首次获得数据：若已有媒体则产生一条 SongChanged。
    /// </summary>
    public static List<PluginEvent> Diff(MediaSnapshot? before, MediaSnapshot after)
    {
        var events = new List<PluginEvent>();

        if (before == null)
        {
            if (HasMedia(after))
            {
                events.Add(new PluginEvent { Type = PluginEventType.SongChanged, Before = null, After = after });
            }
            return events;
        }

        var songChanged = before.Title != after.Title
            || before.Artist != after.Artist
            || before.AlbumTitle != after.AlbumTitle
            || before.SongId != after.SongId
            || before.Source != after.Source;
        if (songChanged)
        {
            events.Add(new PluginEvent { Type = PluginEventType.SongChanged, Before = before, After = after });
        }

        if (before.IsPlaying != after.IsPlaying)
        {
            events.Add(new PluginEvent { Type = PluginEventType.PlaybackStateChanged, Before = before, After = after });
        }

        // 音量按整数比较（0-100 语义），避免浮点抖动
        if ((int)before.Volume != (int)after.Volume || before.Muted != after.Muted)
        {
            events.Add(new PluginEvent { Type = PluginEventType.VolumeChanged, Before = before, After = after });
        }

        return events;
    }
}
