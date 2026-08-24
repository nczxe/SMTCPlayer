namespace SMTCPlayer.PluginApi;

/// <summary>Core 广播的事件类型。</summary>
public enum PluginEventType
{
    /// <summary>切歌：标题/歌手/专辑/歌曲 ID/来源 变化。</summary>
    SongChanged,

    /// <summary>播放状态变化：播放 ↔ 暂停/停止。</summary>
    PlaybackStateChanged,

    /// <summary>音量或静音状态变化。</summary>
    VolumeChanged,

    /// <summary>后端服务启动/停止/连接断开。</summary>
    ServerStateChanged,
}

/// <summary>广播给所有插件的事件。</summary>
public sealed class PluginEvent
{
    /// <summary>事件类型。</summary>
    public PluginEventType Type { get; init; }

    /// <summary>事件产生时间（本地时间）。</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>变化前的快照；首次获得媒体或服务停止时为 null。</summary>
    public MediaSnapshot? Before { get; init; }

    /// <summary>变化后的快照；ServerStateChanged 时为 null。</summary>
    public MediaSnapshot? After { get; init; }

    /// <summary>仅 <see cref="ServerStateChanged"/> 有效：服务是否处于运行状态。</summary>
    public bool IsServerRunning { get; init; }
}

/// <summary>
/// 媒体状态只读快照（宿主轮询数据向插件域的投影）。
/// Position 每秒都在变化，不产生事件；需要实时进度时从 <see cref="IPluginContext.Current"/> 获取。
/// </summary>
public sealed record MediaSnapshot
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string AlbumTitle { get; init; } = "";
    public string Status { get; init; } = "stopped";
    public string? Source { get; init; }
    public bool IsPlaying { get; init; }
    public bool HasPrevious { get; init; }
    public bool HasNext { get; init; }
    public double Position { get; init; }
    public double Duration { get; init; }
    public double Volume { get; init; }
    public bool Muted { get; init; }
    public long? SongId { get; init; }

    /// <summary>空快照（服务未启动/尚无数据）。</summary>
    public static MediaSnapshot Empty { get; } = new();
}
