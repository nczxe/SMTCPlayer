using System.Text.Json;
using SMTCPlayer.PluginApi;

namespace SMTCPlayer.Core.Plugins;

/// <summary>
/// IPluginSettings 的隔离实现：每个插件独立存储于
/// %LocalAppData%/SMTCPlayer/plugins-data/&lt;pluginId&gt;/settings.json。
/// </summary>
internal sealed class PluginSettingsStore : IPluginSettings
{
    private readonly string _file;
    private readonly object _lock = new();
    private Dictionary<string, JsonElement> _data = new();

    public PluginSettingsStore(string pluginId)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SMTCPlayer", "plugins-data", pluginId);
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "settings.json");
        Load();
    }

    public T? Get<T>(string key, T? fallback = default)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(key, out var element)) return fallback;
            try
            {
                return element.Deserialize<T>();
            }
            catch
            {
                return fallback;
            }
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            _data[key] = JsonSerializer.SerializeToElement(value);
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                File.WriteAllText(_file, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // 保存失败不影响运行
            }
        }
    }

    public void Reload()
    {
        lock (_lock)
        {
            Load();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                _data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_file))
                       ?? new Dictionary<string, JsonElement>();
            }
        }
        catch
        {
            _data = new Dictionary<string, JsonElement>();
        }
    }
}
