using System.Runtime.Versioning;
using Microsoft.Win32;
using SMTCPlayer.Core.Services;

namespace SMTCPlayer.Core.LanProtocol;

/// <summary>
/// URI 协议标识常量。与平台无关，供解析侧（<see cref="UriCommandProcessor"/>）安全引用，
/// 避免解析代码因引用 Windows 专用类型而触发平台兼容性告警。
/// </summary>
public static class UriScheme
{
    /// <summary>协议名（URI Scheme）。</summary>
    public const string Name = "smtcplayer";
}

/// <summary>
/// <c>smtcplayer://</c> 自定义 URL 协议的注册器。
/// <para>
/// 在 <b>HKCU</b> <c>\Software\Classes\smtcplayer</c> 下注册（无需管理员权限），
/// 指向当前 WinUI 可执行文件；启动时调用 <see cref="Apply"/> 做幂等自修复：
/// 已存在且命令一致则不重复写入，命令与当前 exe 不一致（如换目录）则重写。
/// </para>
/// <para>所有异常一律吞掉并记 Warn，注册失败不影响应用其余功能。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class UriSchemeRegistrar
{
    /// <summary>协议名（URI Scheme）。</summary>
    public const string SchemeName = UriScheme.Name;

    /// <summary>注册表根路径（相对 HKCU）。</summary>
    private const string BaseKeyPath = @"Software\Classes\" + SchemeName;

    /// <summary>协议说明（写入默认值）。</summary>
    private const string Description = "URL:SMTC Player Protocol";

    /// <summary>由 exe 路径构造 <c>shell\open\command</c> 的命令行。</summary>
    public static string BuildCommand(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>判断当前注册是否已指向给定 exe（命令一致）。</summary>
    public static bool IsRegistered(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;
        try
        {
            using var commandKey = Registry.CurrentUser.OpenSubKey(
                BaseKeyPath + @"\shell\open\command", writable: false);
            var existing = commandKey?.GetValue(null) as string;
            return string.Equals(existing, BuildCommand(exePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取 URI 协议注册状态失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 按设置应用注册状态：<paramref name="enabled"/> 为真时确保注册（幂等），
    /// 为假时注销。返回是否处于期望状态。
    /// </summary>
    public static bool Apply(bool enabled, string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            Logger.Warn("URI 协议注册已跳过：无法确定当前可执行文件路径");
            return false;
        }

        return enabled ? EnsureRegistered(exePath) : Unregister();
    }

    /// <summary>确保协议已注册并指向给定 exe（幂等：一致时不重复写入）。</summary>
    public static bool EnsureRegistered(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return false;

        try
        {
            if (IsRegistered(exePath))
                return true;

            using (var baseKey = Registry.CurrentUser.CreateSubKey(BaseKeyPath, writable: true))
            {
                if (baseKey == null) return false;
                baseKey.SetValue(null, Description);
                baseKey.SetValue("URL Protocol", string.Empty);
            }

            using (var iconKey = Registry.CurrentUser.CreateSubKey(BaseKeyPath + @"\DefaultIcon", writable: true))
                iconKey?.SetValue(null, $"\"{exePath}\",0");

            using (var commandKey = Registry.CurrentUser.CreateSubKey(BaseKeyPath + @"\shell\open\command", writable: true))
                commandKey?.SetValue(null, BuildCommand(exePath));

            Logger.Info($"已注册 URI 协议 {SchemeName}:// → {exePath}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"注册 URI 协议 {SchemeName}:// 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>注销协议（不存在时视为成功）。</summary>
    public static bool Unregister()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(BaseKeyPath, writable: false))
            {
                if (key == null) return true;
            }

            Registry.CurrentUser.DeleteSubKeyTree(BaseKeyPath, throwOnMissingSubKey: false);
            Logger.Info($"已注销 URI 协议 {SchemeName}://");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"注销 URI 协议 {SchemeName}:// 失败: {ex.Message}");
            return false;
        }
    }
}
