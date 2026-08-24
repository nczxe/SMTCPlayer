using System.Text.Json.Serialization;

namespace SMTCPlayer.Core.Plugins;

/// <summary>plugin.json 清单模型（每个插件目录一份）。</summary>
internal sealed class PluginManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("assembly")]
    public string Assembly { get; set; } = "";

    [JsonPropertyName("entryType")]
    public string EntryType { get; set; } = "";

    /// <summary>所需的插件 API（ABI）版本；缺省视为兼容（宽容放行旧插件）。</summary>
    [JsonPropertyName("apiVersion")]
    public int? ApiVersion { get; set; }

    /// <summary>所需的最低宿主应用版本（如 "1.2.0"）；缺省不限制。</summary>
    [JsonPropertyName("minHostVersion")]
    public string? MinHostVersion { get; set; }

    /// <summary>
    /// 插件提供的内容服务能力（如 "search"、"playlists"）。
    /// 宿主聚合所有已启用插件的能力上报后端，网页端据此显隐对应功能区。
    /// </summary>
    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = new();
}
