using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;
using SMTCPlayer.Core.LanProtocol;

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

    /// <summary>更新专辑名显示并保存</summary>
    public static void SetShowAlbum(bool enabled)
    {
        _current.ShowAlbum = enabled;
        Save();
    }

    /// <summary>获取专辑名显示（默认关闭）</summary>
    public static bool GetShowAlbum()
    {
        return _current.ShowAlbum;
    }

    /// <summary>
    /// 获取关闭按钮行为：null=每次询问；"minimize"=最小化到托盘；"exit"=直接退出。
    /// </summary>
    public static string? GetCloseAction() => string.IsNullOrEmpty(_current.CloseAction) ? null : _current.CloseAction;

    public static void SetCloseAction(string? action)
    {
        _current.CloseAction = action;
        Save();
    }

    /// <summary>歌曲进度偏移上限（毫秒）</summary>
    private const int MaxPositionOffsetMs = 5000;

    /// <summary>获取歌曲进度偏移（毫秒，±5000）</summary>
    public static int GetPositionOffsetMs()
    {
        return Math.Clamp(_current.PositionOffsetMs, -MaxPositionOffsetMs, MaxPositionOffsetMs);
    }

    /// <summary>更新歌曲进度偏移并保存（毫秒，±5000）</summary>
    public static void SetPositionOffsetMs(int ms)
    {
        _current.PositionOffsetMs = Math.Clamp(ms, -MaxPositionOffsetMs, MaxPositionOffsetMs);
        Save();
    }

    /// <summary>局域网协议端口下限（1024）与上限（65535）。</summary>
    private const int MinLanPort = 1024;
    private const int MaxLanPort = 65535;

    /// <summary>是否启用局域网协议服务（默认关闭）。</summary>
    public static bool GetLanEnabled() => _current.LanEnabled;

    /// <summary>更新局域网协议服务开关并保存。</summary>
    public static void SetLanEnabled(bool enabled)
    {
        _current.LanEnabled = enabled;
        Save();
    }

    /// <summary>获取局域网协议监听端口（1024-65535，默认 9000）。</summary>
    public static int GetLanPort() => Math.Clamp(_current.LanPort, MinLanPort, MaxLanPort);

    /// <summary>更新局域网协议监听端口并保存（1024-65535）。</summary>
    public static void SetLanPort(int port)
    {
        _current.LanPort = Math.Clamp(port, MinLanPort, MaxLanPort);
        Save();
    }

    /// <summary>获取监听范围："loopback"=仅本机；"all"=全网卡。默认 loopback。</summary>
    public static string GetLanScope() => NormalizeLanScope(_current.LanScope);

    /// <summary>更新监听范围并保存（仅接受 "loopback" / "all"）。</summary>
    public static void SetLanScope(string? scope)
    {
        _current.LanScope = NormalizeLanScope(scope);
        Save();
    }

    private static string NormalizeLanScope(string? scope) =>
        string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase) ? "all" : "loopback";

    /// <summary>是否注册 <c>smtcplayer://</c> 自定义 URL 协议（默认开启）。</summary>
    public static bool GetUriSchemeEnabled() => _current.UriSchemeEnabled;

    /// <summary>更新 URI 协议注册开关并保存。</summary>
    public static void SetUriSchemeEnabled(bool enabled)
    {
        _current.UriSchemeEnabled = enabled;
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
    public bool ShowAlbum { get; set; } = false;

    /// <summary>关闭按钮行为：null=每次询问；"minimize"=最小化到托盘；"exit"=直接退出。</summary>
    public string? CloseAction { get; set; } = null;

    /// <summary>歌曲进度偏移（毫秒）：校准网易云监视器读取的播放进度，±5000。</summary>
    public int PositionOffsetMs { get; set; } = 0;

    /// <summary>是否启用局域网协议服务（默认关闭）。</summary>
    public bool LanEnabled { get; set; } = false;

    /// <summary>局域网协议监听端口，默认 9000。</summary>
    public int LanPort { get; set; } = LanProtocolOptions.DefaultPort;

    /// <summary>监听范围："loopback"=仅本机；"all"=全网卡。默认 loopback。</summary>
    public string LanScope { get; set; } = "loopback";

    /// <summary>是否注册 <c>smtcplayer://</c> 自定义 URL 协议（默认开启）。</summary>
    public bool UriSchemeEnabled { get; set; } = true;
}
