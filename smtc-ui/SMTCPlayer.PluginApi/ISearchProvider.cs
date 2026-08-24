namespace SMTCPlayer.PluginApi;

/// <summary>
/// 搜索提供者（可选能力接口）：实现它的插件可承接网页端的搜索与播放请求。
/// 宿主把网页端发来的请求经任务队列转给对应插件（按 plugin.json 的 Id 路由），
/// 搜索实现与播放方式完全由插件自行决定（每个音乐软件方法不同）。
/// </summary>
public interface ISearchProvider
{
    /// <summary>按关键词搜索，返回统一结构的结果列表（失败抛异常，消息会展示给网页用户）。</summary>
    Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken);

    /// <summary>在系统已打开的音乐软件中播放指定结果（用各自软件的方法）。</summary>
    Task<bool> PlayAsync(SearchResultItem item, CancellationToken cancellationToken);
}

/// <summary>统一搜索结果项（宿主与网页端之间的标准负载）。</summary>
public sealed record SearchResultItem
{
    /// <summary>提供者内部的曲目标识（网易云 song_id / Spotify track id 等）。</summary>
    public required string Id { get; init; }

    public string Name { get; init; } = "";

    /// <summary>显示用歌手串（已拼接）。</summary>
    public string Artists { get; init; } = "";

    public string Album { get; init; } = "";

    /// <summary>时长（毫秒）。</summary>
    public double Duration { get; init; }

    /// <summary>封面 URL（可选）。</summary>
    public string? Cover { get; init; }
}
