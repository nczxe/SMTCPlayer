using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace SMTCPlayer.WinUI.Services;

/// <summary>
/// 应用设置的持久化存储，保存到 %LocalAppData%/SMTCPlayer/settings.json
/// </summary>
public static class AppSettings
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SMTCPlayer");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>当前内存中的设置</summary>
    private static SettingsData _current = new();

    /// <summary>获取当前设置</summary>
    public static SettingsData Current => _current;

    /// <summary>从磁盘加载设置</summary>
    public static SettingsData Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                _current = JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
            }
            else
            {
                _current = new SettingsData();
            }
        }
        catch
        {
            _current = new SettingsData();
        }
        return _current;
    }

    /// <summary>保存设置到磁盘</summary>
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(_current, JsonOpts);
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // 保存失败不影响运行
        }
    }

    /// <summary>更新主题并保存</summary>
    public static void SetTheme(ElementTheme theme)
    {
        _current.Theme = theme.ToString();
        Save();
    }

    /// <summary>更新调试模式并保存</summary>
    public static void SetDebugMode(bool enabled)
    {
        _current.DebugMode = enabled;
        Save();
    }

    /// <summary>获取主题</summary>
    public static ElementTheme GetTheme()
    {
        return _current.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>获取调试模式</summary>
    public static bool GetDebugMode()
    {
        return _current.DebugMode;
    }
}

/// <summary>设置数据模型</summary>
public class SettingsData
{
    public string Theme { get; set; } = "Default";
    public bool DebugMode { get; set; } = false;
}
