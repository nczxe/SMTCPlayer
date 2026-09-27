using System.Text.Json.Serialization;
using SMTCPlayer.PluginApi;

namespace SMTCPlayer.PluginSystem;

/// <summary>后端任务队列下发给宿主的插件调用任务（网页端发起的搜索/播放）。</summary>
internal sealed class PluginJob
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("query")]
    public string? Query { get; set; }

    [JsonPropertyName("item")]
    public SearchResultItem? Item { get; set; }
}
