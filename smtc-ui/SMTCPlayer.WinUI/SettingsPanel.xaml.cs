using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SMTCPlayer.Core.Services;
using SMTCPlayer.PluginSystem;
using SMTCPlayer.WinUI.Services;
using System.Diagnostics;

namespace SMTCPlayer.WinUI;

/// <summary>
/// 全窗口设置面板（替代原 ContentDialog 版 SettingsDialog）。
/// 由 MainWindow 以覆盖层方式承载；主题/专辑名等状态变化通过事件通知宿主即时应用。
/// </summary>
public sealed partial class SettingsPanel : UserControl
{
    /// <summary>用户选择的主题。</summary>
    public ElementTheme SelectedTheme { get; private set; } = ElementTheme.Default;

    /// <summary>是否开启调试模式。</summary>
    public bool IsDebugMode { get; private set; }

    /// <summary>是否显示专辑名称。</summary>
    public bool ShowAlbum { get; private set; }

    /// <summary>主题或专辑名显示变化（宿主应即时应用）。</summary>
    public event Action? StateChanged;

    /// <summary>用户点击返回按钮，宿主应关闭面板。</summary>
    public event Action? CloseRequested;

    /// <summary>用户请求修改 PIN。</summary>
    public event Action? PinRequested;

    /// <summary>用户请求打开关于对话框。</summary>
    public event Action? AboutRequested;

    /// <summary>用户请求校准歌曲进度偏移。</summary>
    public event Action? PositionOffsetRequested;

    /// <summary>用户请求打开插件管理窗口。</summary>
    public event Action? PluginManagerRequested;

    /// <summary>用户请求打开日志查看器。</summary>
    public event Action? LogViewerRequested;

    /// <summary>是否启用局域网协议服务。</summary>
    public bool LanEnabled { get; private set; }

    /// <summary>局域网协议监听端口。</summary>
    public int LanPort { get; private set; } = 9000;

    /// <summary>监听范围："loopback"=仅本机；"all"=全网卡。</summary>
    public string LanScope { get; private set; } = "loopback";

    /// <summary>是否注册 smtcplayer:// 自定义 URL 协议。</summary>
    public bool UriSchemeEnabled { get; private set; } = true;

    /// <summary>局域网协议配置变化（宿主应重启服务以生效）。</summary>
    public event Action? LanSettingsChanged;

    /// <summary>URI 协议注册开关变化（宿主应注册/注销协议）。</summary>
    public event Action? UriSchemeSettingChanged;

    private PluginManager? _pluginManager;
    private bool _loadingLan;

    public SettingsPanel()
    {
        InitializeComponent();
    }

    /// <summary>每次打开面板时由宿主调用：注入状态与插件宿主。</summary>
    public void LoadState(ElementTheme currentTheme, PluginManager? pluginManager)
    {
        _pluginManager = pluginManager;
        SelectedTheme = currentTheme;
        IsDebugMode = AppSettings.GetDebugMode();

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

        DebugModeToggle.IsOn = IsDebugMode;
        ShowAlbum = AppSettings.GetShowAlbum();
        ShowAlbumToggle.IsOn = ShowAlbum;

        CloseActionSelector.SelectedIndex = AppSettings.GetCloseAction() switch
        {
            "minimize" => 1,
            "exit" => 2,
            _ => 0,
        };

        UpdateStatusText.Text = $"当前版本 v{UpdateChecker.CurrentVersion}";
        UpdatePositionOffsetText(AppSettings.GetPositionOffsetMs());

        PluginCountText.Text = _pluginManager != null
            ? $"共 {_pluginManager.Plugins.Count} 个插件"
            : "共 0 个插件";

        _loadingLan = true;
        LanEnabled = AppSettings.GetLanEnabled();
        LanPort = AppSettings.GetLanPort();
        LanScope = AppSettings.GetLanScope();
        LanEnabledToggle.IsOn = LanEnabled;
        LanPortBox.Value = LanPort;
        LanScopeSelector.SelectedIndex = LanScope == "all" ? 1 : 0;
        UriSchemeEnabled = AppSettings.GetUriSchemeEnabled();
        UriSchemeToggle.IsOn = UriSchemeEnabled;
        UpdateLanControlsEnabled();
        _loadingLan = false;
    }

    // ============== 外观 / 行为 ==============

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeSystem is null) return;
        if (ThemeSystem.IsChecked == true) SelectedTheme = ElementTheme.Default;
        else if (ThemeLight.IsChecked == true) SelectedTheme = ElementTheme.Light;
        else if (ThemeDark.IsChecked == true) SelectedTheme = ElementTheme.Dark;
        AppSettings.SetTheme(SelectedTheme);
        StateChanged?.Invoke();
    }

    private void CloseActionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CloseActionSelector is null) return;
        AppSettings.SetCloseAction(CloseActionSelector.SelectedIndex switch
        {
            1 => "minimize",
            2 => "exit",
            _ => null,
        });
    }

    private void ShowAlbumToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ShowAlbumToggle is null) return;
        ShowAlbum = ShowAlbumToggle.IsOn;
        AppSettings.SetShowAlbum(ShowAlbum);
        Logger.Info($"专辑名显示已{(ShowAlbum ? "开启" : "关闭")}");
        StateChanged?.Invoke();
    }

    private void DebugModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (DebugModeToggle is null) return;
        IsDebugMode = DebugModeToggle.IsOn;
        Logger.MinLevel = IsDebugMode ? LogLevel.Debug : LogLevel.Info;
        AppSettings.SetDebugMode(IsDebugMode);
        Logger.Info($"调试模式已{(IsDebugMode ? "开启" : "关闭")}");
    }

    // ============== 局域网与外部协议 ==============

    private void LanEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (LanEnabledToggle is null || _loadingLan) return;
        LanEnabled = LanEnabledToggle.IsOn;
        AppSettings.SetLanEnabled(LanEnabled);
        UpdateLanControlsEnabled();
        Logger.Info($"局域网协议服务已{(LanEnabled ? "开启" : "关闭")}");
        LanSettingsChanged?.Invoke();
    }

    private void LanPortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (LanPortBox is null || _loadingLan) return;
        if (double.IsNaN(args.NewValue)) return;
        var port = (int)args.NewValue;
        if (port < 1024 || port > 65535) return;
        if (port == LanPort) return;
        LanPort = port;
        AppSettings.SetLanPort(port);
        Logger.Info($"局域网协议监听端口已设为 {port}");
        LanSettingsChanged?.Invoke();
    }

    private void LanScopeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanScopeSelector is null || _loadingLan) return;
        var scope = LanScopeSelector.SelectedIndex == 1 ? "all" : "loopback";
        if (scope == LanScope) return;
        LanScope = scope;
        AppSettings.SetLanScope(scope);
        Logger.Info($"局域网协议监听范围已设为 {(scope == "all" ? "全网卡" : "仅本机")}");
        LanSettingsChanged?.Invoke();
    }

    private void UpdateLanControlsEnabled()
    {
        if (LanPortBox is null) return;
        LanPortBox.IsEnabled = LanEnabled;
        LanScopeSelector.IsEnabled = LanEnabled;
    }

    private void UriSchemeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (UriSchemeToggle is null || _loadingLan) return;
        UriSchemeEnabled = UriSchemeToggle.IsOn;
        AppSettings.SetUriSchemeEnabled(UriSchemeEnabled);
        Logger.Info($"smtcplayer:// 协议注册已{(UriSchemeEnabled ? "开启" : "关闭")}");
        UriSchemeSettingChanged?.Invoke();
    }

    // ============== 跳转按钮 ==============

    private void BackButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void ChangePinButton_Click(object sender, RoutedEventArgs e) => PinRequested?.Invoke();

    private void AboutButton_Click(object sender, RoutedEventArgs e) => AboutRequested?.Invoke();

    private void PluginManagerButton_Click(object sender, RoutedEventArgs e) => PluginManagerRequested?.Invoke();

    // ============== 播放进度校准 ==============

    private void CalibrateOffsetButton_Click(object sender, RoutedEventArgs e) =>
        PositionOffsetRequested?.Invoke();

    /// <summary>刷新“当前偏移”显示（宿主在保存校准后调用）。</summary>
    public void UpdatePositionOffsetText(int ms)
    {
        if (PositionOffsetText is null) return;
        PositionOffsetText.Text = $"当前偏移：{(ms >= 0 ? "+" : "")}{ms} ms";
    }

    // ============== 检查更新 ==============

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        UpdateNotesText.Visibility = Visibility.Collapsed;
        ReleaseLink.Visibility = Visibility.Collapsed;
        try
        {
            var checker = new UpdateChecker();
            var result = await checker.CheckNowAsync();
            UpdateStatusText.Text = result.Status == UpdateCheckStatus.UpdateAvailable
                ? $"{result.Message} 请前往发布页手动下载。"
                : result.Message;
            ReleaseLink.Visibility = result.Status == UpdateCheckStatus.UpdateAvailable
                ? Visibility.Visible
                : Visibility.Collapsed;
            ShowUpdateNotes(result.Update?.ReleaseNotes);
        }
        catch (Exception ex)
        {
            Logger.Warn($"检查更新异常: {ex.Message}");
            UpdateStatusText.Text = $"检查失败: {ex.Message}";
            UpdateNotesText.Visibility = Visibility.Collapsed;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>展示本次更新的要点（来自站点 update.json 的 releaseNotes）。</summary>
    private void ShowUpdateNotes(IReadOnlyList<string>? notes)
    {
        if (notes is not { Count: > 0 })
        {
            UpdateNotesText.Visibility = Visibility.Collapsed;
            return;
        }

        var lines = new List<string> { "本次更新内容：" };
        foreach (var n in notes) lines.Add("• " + n);
        UpdateNotesText.Text = string.Join("\n", lines);
        UpdateNotesText.Visibility = Visibility.Visible;
    }

    private void ReleaseLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateChecker.ReleasesPageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"打开发布页失败: {ex.Message}");
        }
    }

    /// <summary>查看更新日志：读取站点 update.json（CHANGELOG 同源），弹窗展示"这次更新了什么"。</summary>
    private async void ViewChangelog_Click(object sender, RoutedEventArgs e)
    {
        ViewChangelogButton.IsEnabled = false;
        try
        {
            var checker = new UpdateChecker();
            var manifest = await checker.FetchManifestAsync();
            if (manifest is null || manifest.ReleaseNotes.Count == 0)
            {
                await ShowInfoDialogAsync("更新日志", "暂时无法获取更新日志，请稍后重试或前往网页查看。");
                return;
            }

            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(manifest.ReleaseDate)
                    ? $"v{manifest.LatestVersion}"
                    : $"v{manifest.LatestVersion} · {manifest.ReleaseDate}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            foreach (var note in manifest.ReleaseNotes)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "• " + note,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            panel.Children.Add(new HyperlinkButton
            {
                Content = "在网页查看完整日志",
                NavigateUri = new Uri(string.IsNullOrWhiteSpace(manifest.UpdateUrl)
                    ? UpdateChecker.UpdatePageUrl
                    : manifest.UpdateUrl),
                FontSize = 12,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 4, 0, 0),
            });

            var dlg = new ContentDialog
            {
                Title = "更新日志",
                Content = new ScrollViewer
                {
                    Content = panel,
                    MaxHeight = 380,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollMode = ScrollMode.Disabled,
                },
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"查看更新日志失败: {ex.Message}");
            await ShowInfoDialogAsync("更新日志", $"获取失败: {ex.Message}");
        }
        finally
        {
            ViewChangelogButton.IsEnabled = true;
        }
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        await dlg.ShowAsync();
    }

    // ============== 日志工具 ==============

    private void OpenLogViewerButton_Click(object sender, RoutedEventArgs e) => LogViewerRequested?.Invoke();

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
