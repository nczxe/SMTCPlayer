using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SMTCPlayer.Core.Plugins;
using SMTCPlayer.Core.Services;
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

    private PluginHost? _pluginHost;

    public SettingsPanel()
    {
        InitializeComponent();
    }

    /// <summary>每次打开面板时由宿主调用：注入状态与插件宿主。</summary>
    public void LoadState(ElementTheme currentTheme, PluginHost? pluginHost)
    {
        _pluginHost = pluginHost;
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

        PluginListPanel.Children.Clear();
        BuildPluginList();
    }

    // ============== 插件列表（纯 C# 构建） ==============

    private void BuildPluginList()
    {
        if (_pluginHost == null) return;

        var plugins = _pluginHost.Plugins;
        if (plugins.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "未发现插件。将插件文件夹（含 plugin.json）放入 plugins 目录即可自动加载。",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
            };
            PluginListPanel.Children.Add(empty);
            return;
        }

        foreach (var plugin in plugins)
        {
            PluginListPanel.Children.Add(BuildPluginItem(plugin));
        }
    }

    private Border BuildPluginItem(PluginInfo plugin)
    {
        var header = string.IsNullOrEmpty(plugin.Version)
            ? plugin.Name
            : $"{plugin.Name}  v{plugin.Version}";

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titlePanel.Children.Add(new TextBlock
        {
            Text = header,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        if (!string.IsNullOrEmpty(plugin.Author))
        {
            titlePanel.Children.Add(new TextBlock
            {
                Text = $"by {plugin.Author}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Bottom,
            });
        }

        var item = new StackPanel { Spacing = 4 };
        item.Children.Add(titlePanel);
        item.Children.Add(new TextBlock
        {
            Text = plugin.Error != null ? $"加载失败: {plugin.Error}" : plugin.Description,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = plugin.Error != null ? 0.9 : 0.7,
            TextWrapping = TextWrapping.Wrap,
        });

        var toggle = new ToggleSwitch
        {
            IsOn = plugin.IsEnabled,
            OnContent = "启用",
            OffContent = "停用",
            MinWidth = 120,
            Tag = plugin.Id,
        };
        toggle.Toggled += PluginToggle_Toggled;

        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(item, 0);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(item);
        row.Children.Add(toggle);

        return new Border
        {
            Child = row,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
        };
    }

    private async void PluginToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_pluginHost == null || sender is not ToggleSwitch toggle) return;
        if (toggle.Tag is not string pluginId) return;

        try
        {
            await _pluginHost.SetPluginEnabledAsync(pluginId, toggle.IsOn);
        }
        catch (Exception ex)
        {
            Logger.Warn($"切换插件状态失败 {pluginId}: {ex.Message}");
        }
    }

    private async void RescanPlugins_Click(object sender, RoutedEventArgs e)
    {
        if (_pluginHost == null) return;

        try
        {
            var added = await _pluginHost.RescanAsync();
            PluginListPanel.Children.Clear();
            BuildPluginList();
            PluginHintText.Text = added > 0
                ? $"扫描完成：新加载 {added} 个插件。"
                : "扫描完成：没有发现新插件。";
            Logger.Info($"设置页手动重扫插件: 新增 {added} 个");
        }
        catch (Exception ex)
        {
            Logger.Warn($"重新扫描插件失败: {ex.Message}");
            PluginHintText.Text = $"扫描失败: {ex.Message}";
        }
    }

    private void OpenPluginsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(PluginHost.UserPluginsRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{PluginHost.UserPluginsRoot}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"打开插件文件夹失败: {ex.Message}");
        }
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

    // ============== 跳转按钮 ==============

    private void BackButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void ChangePinButton_Click(object sender, RoutedEventArgs e) => PinRequested?.Invoke();

    private void AboutButton_Click(object sender, RoutedEventArgs e) => AboutRequested?.Invoke();

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
        }
        catch (Exception ex)
        {
            Logger.Warn($"检查更新异常: {ex.Message}");
            UpdateStatusText.Text = $"检查失败: {ex.Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
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

    // ============== 日志工具 ==============

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
