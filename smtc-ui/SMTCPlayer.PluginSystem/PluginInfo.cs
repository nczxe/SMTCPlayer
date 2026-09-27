namespace SMTCPlayer.PluginSystem;

/// <summary>插件描述信息（供宿主与设置界面使用）。</summary>
public sealed class PluginInfo
{
    public required string Id { get; init; }
    public string Name { get; internal set; } = "";
    public string Version { get; internal set; } = "";
    public string Author { get; internal set; } = "";
    public string Description { get; internal set; } = "";
    /// <summary>插件所在目录（热更新重扫后可能指向新位置）。</summary>
    public string Directory { get; internal set; } = "";

    /// <summary>是否启用（运行时状态，持久化到 plugins.json）。</summary>
    public bool IsEnabled { get; internal set; }

    /// <summary>是否已成功实例化并收到 OnLoaded。</summary>
    public bool IsLoaded { get; internal set; }

    /// <summary>加载失败原因（成功时为 null）。</summary>
    public string? Error { get; internal set; }

    /// <summary>声明的内容服务能力（如 search / playlists），来自 plugin.json。</summary>
    public IReadOnlyList<string> Capabilities { get; internal set; } = Array.Empty<string>();
}
