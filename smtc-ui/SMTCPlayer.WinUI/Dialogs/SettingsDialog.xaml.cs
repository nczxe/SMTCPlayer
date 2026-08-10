using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SMTCPlayer.Core.Services;
using SMTCPlayer.WinUI.Services;
using System.Diagnostics;

namespace SMTCPlayer.WinUI.Dialogs;

public sealed partial class SettingsDialog : ContentDialog
{
    /// <summary>用户选择的主题。</summary>
    public ElementTheme SelectedTheme { get; private set; } = ElementTheme.Default;

    /// <summary>是否开启调试模式。</summary>
    public bool IsDebugMode { get; private set; }

    /// <summary>用户是否点击了"修改 PIN"。</summary>
    public bool WantsChangePin { get; set; }

    /// <summary>用户是否点击了"清除网易云 Cookie"。</summary>
    public bool WantsClearCookies { get; set; }

    /// <summary>用户是否点击了"关于"。</summary>
    public bool WantsAbout { get; set; }

    public SettingsDialog(ElementTheme currentTheme, bool debugMode)
    {
        InitializeComponent();

        SelectedTheme = currentTheme;
        IsDebugMode = debugMode;

        switch (currentTheme)
        {
            case ElementTheme.Light:
                ThemeLight.IsChecked = true;
                break;
            case ElementTheme.Dark:
                ThemeDark.IsChecked = true;
                break;
            default:
                ThemeSystem.IsChecked = true;
                break;
        }

        DebugModeToggle.IsOn = debugMode;
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeSystem is null) return;
        if (ThemeSystem.IsChecked == true) SelectedTheme = ElementTheme.Default;
        else if (ThemeLight.IsChecked == true) SelectedTheme = ElementTheme.Light;
        else if (ThemeDark.IsChecked == true) SelectedTheme = ElementTheme.Dark;
        // 持久化保存
        AppSettings.SetTheme(SelectedTheme);
    }

    private void DebugModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (DebugModeToggle is null) return;
        IsDebugMode = DebugModeToggle.IsOn;
        Logger.MinLevel = IsDebugMode ? LogLevel.Debug : LogLevel.Info;
        // 持久化保存
        AppSettings.SetDebugMode(IsDebugMode);
        Logger.Info($"调试模式已{(IsDebugMode ? "开启" : "关闭")}");
    }

    private void ChangePinButton_Click(object sender, RoutedEventArgs e)
    {
        WantsChangePin = true;
        Hide();
    }

    private void ClearCookiesButton_Click(object sender, RoutedEventArgs e)
    {
        WantsClearCookies = true;
        Hide();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        WantsAbout = true;
        Hide();
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logDir = Path.GetDirectoryName(Logger.LogFilePath) ?? "";
            if (!string.IsNullOrEmpty(logDir) && Directory.Exists(logDir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", logDir) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"打开日志文件夹失败: {ex.Message}");
        }
    }

    private void OpenDebugConsole_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logFile = Logger.LogFilePath;
            if (string.IsNullOrEmpty(logFile) || !File.Exists(logFile))
            {
                Logger.Warn("日志文件不存在，无法启动调试控制台");
                return;
            }

            // 启动 PowerShell 窗口，实时跟踪日志输出
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -Command \"Write-Host 'SMTC Player 调试控制台' -ForegroundColor Cyan; Write-Host '实时日志输出:'; Write-Host ''; Get-Content -Path '{logFile}' -Wait -Tail 50\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            Process.Start(psi);
            Logger.Info("调试控制台已启动");
        }
        catch (Exception ex)
        {
            Logger.Warn($"启动调试控制台失败: {ex.Message}");
        }
    }
}
