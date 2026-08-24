using System.Text.Json;
using System.Text.Json.Serialization;

namespace SMTCPlayer.Core.Plugins;

/// <summary>
/// 插件启用状态注册表，持久化到 %LocalAppData%/SMTCPlayer/plugins.json。
/// 存"禁用列表"：新发现的插件默认启用。
/// </summary>
internal sealed class PluginRegistry
{
    private static readonly string RegistryFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SMTCPlayer", "plugins.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _lock = new();
    private HashSet<string> _disabled = new();

    public PluginRegistry()
    {
        Load();
    }

    public bool IsEnabled(string pluginId)
    {
        lock (_lock) return !_disabled.Contains(pluginId);
    }

    /// <summary>更新启用状态并立即持久化。</summary>
    public void SetEnabled(string pluginId, bool enabled)
    {
        lock (_lock)
        {
            if (enabled) _disabled.Remove(pluginId);
            else _disabled.Add(pluginId);
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(RegistryFile))
            {
                var data = JsonSerializer.Deserialize<RegistryData>(File.ReadAllText(RegistryFile));
                _disabled = data?.Disabled != null ? new HashSet<string>(data.Disabled) : new HashSet<string>();
            }
        }
        catch
        {
            _disabled = new HashSet<string>();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RegistryFile)!);
            var data = new RegistryData { Disabled = _disabled.OrderBy(x => x).ToList() };
            File.WriteAllText(RegistryFile, JsonSerializer.Serialize(data, JsonOpts));
        }
        catch
        {
            // 持久化失败不影响运行
        }
    }

    private sealed class RegistryData
    {
        [JsonPropertyName("disabled")]
        public List<string>? Disabled { get; set; }
    }
}
