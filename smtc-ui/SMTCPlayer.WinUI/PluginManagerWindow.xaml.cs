using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SMTCPlayer.Core.Services;
using SMTCPlayer.PluginSystem;
using Windows.Storage.Pickers;

namespace SMTCPlayer.WinUI;

/// <summary>
/// 独立插件管理窗口：安装(.zip)/卸载/启停/重扫，卡片式列表展示运行状态与加载错误。
/// 仅依赖 PluginSystem 公开 API（PluginManager / PluginInstaller / PluginInfo）。
/// 单例持有与生命周期由 MainWindow 负责；本窗口关闭即由宿主清引用。
/// </summary>
public sealed partial class PluginManagerWindow : Window
{
    private readonly PluginManager _manager;
    private readonly PluginInstaller _installer;
    private bool _busy;

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    /// <param name="pluginManager">插件宿主（来自 MainViewModel.PluginManager）。</param>
    /// <param name="theme">跟随主窗口的当前主题。</param>
    public PluginManagerWindow(PluginManager pluginManager, ElementTheme theme)
    {
        InitializeComponent();

        _manager = pluginManager ?? throw new ArgumentNullException(nameof(pluginManager));
        _installer = new PluginInstaller(_manager);

        Title = "插件管理";
        RootGrid.RequestedTheme = theme;
        TryEnableMicaBackdrop();
        ResizeToLogical(720, 560);

        RefreshList();
        SetStatus($"共 {_manager.Plugins.Count} 个插件");
    }

    // ============== 窗口基础 ==============

    private void TryEnableMicaBackdrop()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }
        catch
        {
            // 旧版本不支持 MicaBackdrop，忽略
        }
    }

    /// <summary>按逻辑像素设置初始窗口尺寸（AppWindow.Resize 使用物理像素，需按 DPI 换算）。</summary>
    private void ResizeToLogical(int width, int height)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var dpi = GetDpiForWindow(hwnd);
            if (dpi <= 0) dpi = 96;
            var scale = dpi / 96.0;
            AppWindow.Resize(new Windows.Graphics.SizeInt32
            {
                Width = (int)Math.Round(width * scale),
                Height = (int)Math.Round(height * scale),
            });
        }
        catch
        {
            try { AppWindow.Resize(new Windows.Graphics.SizeInt32 { Width = width, Height = height }); }
            catch { /* 尺寸设置失败不阻塞窗口 */ }
        }
    }

    /// <summary>操作期间的防重入：禁用工具栏与列表交互。</summary>
    private void SetBusy(bool busy)
    {
        _busy = busy;
        InstallButton.IsEnabled = !busy;
        RescanButton.IsEnabled = !busy;
        OpenFolderButton.IsEnabled = !busy;
        PluginList.IsEnabled = !busy;
    }

    /// <summary>操作结果/错误反馈文本（isError=true 时以警示色显示）。</summary>
    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? GetBrush("SystemFillColorCriticalBrush", "TextFillColorPrimaryBrush")
            : GetBrush("TextFillColorSecondaryBrush", "TextFillColorPrimaryBrush");
    }

    private static Brush GetBrush(string key, string fallbackKey)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            return brush;
        return (Brush)Application.Current.Resources[fallbackKey];
    }

    // ============== 插件列表 ==============

    /// <summary>重建插件列表（每次安装/卸载/启停/重扫后调用）。</summary>
    private void RefreshList()
    {
        PluginList.Items.Clear();

        var plugins = _manager.Plugins;
        if (plugins.Count == 0)
        {
            PluginList.Items.Add(new TextBlock
            {
                Text = "未发现插件。点击「安装插件(.zip)」安装，或将插件文件夹放入插件目录后点击「重新扫描」。",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = GetBrush("TextFillColorSecondaryBrush", "TextFillColorPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 8, 4, 8),
            });
            return;
        }

        foreach (var plugin in plugins)
            PluginList.Items.Add(BuildPluginItem(plugin));
    }

    private Border BuildPluginItem(PluginInfo plugin)
    {
        var isBuiltIn = PluginInstaller.IsBuiltIn(plugin.Directory);

        // 标题行：名称+版本（SemiBold）、作者、状态徽标
        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var header = string.IsNullOrEmpty(plugin.Version)
            ? plugin.Name
            : $"{plugin.Name}  v{plugin.Version}";
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
        titlePanel.Children.Add(BuildStatusBadge(plugin));

        // 描述 / 加载失败原因（红色错误文本）
        var desc = new TextBlock
        {
            Text = plugin.Error != null ? $"加载失败: {plugin.Error}" : plugin.Description,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = plugin.Error != null ? 1.0 : 0.7,
            TextWrapping = TextWrapping.Wrap,
        };
        if (plugin.Error != null)
            desc.Foreground = GetBrush("SystemFillColorCriticalBrush", "TextFillColorPrimaryBrush");

        // 操作行：卸载 / 打开数据目录 / 打开插件目录
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var uninstall = new Button
        {
            Content = "卸载",
            FontSize = 12,
            MinHeight = 28,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(4),
            Tag = plugin,
        };
        if (isBuiltIn)
        {
            uninstall.IsEnabled = false;
            ToolTipService.SetToolTip(uninstall, "内置插件不支持卸载");
        }
        uninstall.Click += UninstallButton_Click;
        actions.Children.Add(uninstall);

        var openData = new Button
        {
            Content = "打开数据目录",
            FontSize = 12,
            MinHeight = 28,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(4),
            Tag = plugin,
        };
        openData.Click += OpenDataFolderButton_Click;
        actions.Children.Add(openData);

        var openDir = new Button
        {
            Content = "打开插件目录",
            FontSize = 12,
            MinHeight = 28,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(4),
            Tag = plugin,
        };
        openDir.Click += OpenPluginFolderButton_Click;
        actions.Children.Add(openDir);

        var info = new StackPanel { Spacing = 4 };
        info.Children.Add(titlePanel);
        info.Children.Add(desc);
        info.Children.Add(actions);

        // 启停开关：IsOn 先赋值再订阅，避免程序化赋值触发 Toggled
        var toggle = new ToggleSwitch
        {
            IsOn = plugin.IsEnabled,
            OnContent = "启用",
            OffContent = "停用",
            MinWidth = 110,
            Tag = plugin.Id,
        };
        toggle.Toggled += PluginToggle_Toggled;

        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(info, 0);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(info);
        row.Children.Add(toggle);

        return new Border
        {
            Child = row,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(8),
            Background = GetBrush("CardBackgroundFillColorDefaultBrush", "ApplicationPageBackgroundThemeBrush"),
            BorderBrush = GetBrush("CardStrokeColorDefaultBrush", "CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>状态徽标：运行中 / 已禁用 / 加载失败。</summary>
    private static Border BuildStatusBadge(PluginInfo plugin)
    {
        string text;
        string colorKey;
        if (!plugin.IsEnabled)
        {
            text = "已禁用";
            colorKey = "TextFillColorSecondaryBrush";
        }
        else if (plugin.Error != null || !plugin.IsLoaded)
        {
            text = "加载失败";
            colorKey = "SystemFillColorCriticalBrush";
        }
        else
        {
            text = "运行中";
            colorKey = "SystemFillColorSuccessBrush";
        }

        var fg = GetBrush(colorKey, "TextFillColorPrimaryBrush");
        return new Border
        {
            BorderBrush = fg,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 1, 8, 2),
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = fg },
        };
    }

    // ============== 启停 ==============

    private async void PluginToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_busy) return; // 操作期间列表重建不触发
        if (sender is not ToggleSwitch toggle || toggle.Tag is not string pluginId) return;

        try
        {
            await _manager.SetPluginEnabledAsync(pluginId, toggle.IsOn);
            SetStatus(toggle.IsOn ? $"已启用插件：{pluginId}" : $"已停用插件：{pluginId}");
            Logger.Info($"插件管理窗口: 插件 {pluginId} 已{(toggle.IsOn ? "启用" : "停用")}");
        }
        catch (Exception ex)
        {
            SetStatus($"切换插件状态失败：{ex.Message}", isError: true);
            Logger.Warn($"切换插件状态失败 {pluginId}: {ex.Message}");
        }
        finally
        {
            RefreshList(); // 刷新状态徽标
        }
    }

    // ============== 安装 ==============

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        string zipPath;
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List,
            };
            // WinUI 3 桌面应用：Picker 需与窗口句柄关联，否则无法弹出
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".zip");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return; // 用户取消
            zipPath = file.Path;
        }
        catch (Exception ex)
        {
            SetStatus($"打开文件选择器失败：{ex.Message}", isError: true);
            return;
        }

        await InstallFromZipAsync(zipPath);
    }

    private async Task InstallFromZipAsync(string zipPath)
    {
        SetBusy(true);
        try
        {
            SetStatus($"正在安装 {Path.GetFileName(zipPath)}…");
            var confirmed = false;
            while (true)
            {
                var result = await _installer.InstallFromZipAsync(zipPath,
                    new PluginInstallOptions { OverwriteSameVersion = confirmed });

                // 同版本冲突：确认后以 OverwriteSameVersion 重装一次；
                // 其余 Id 冲突（如内置插件占用）不可覆盖，直接展示原因
                if (result.Status == PluginInstallStatus.IdConflict && !confirmed
                    && result.Error != null && result.Error.Contains("相同版本"))
                {
                    if (await ConfirmOverwriteAsync(result.Error))
                    {
                        confirmed = true;
                        continue;
                    }
                    SetStatus("已取消安装。");
                    return;
                }

                ApplyInstallResult(result);
                return;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"安装失败：{ex.Message}", isError: true);
            Logger.Warn($"安装插件包异常: {zipPath}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            RefreshList();
        }
    }

    /// <summary>同版本覆盖确认对话框。返回 true 表示用户确认覆盖重装。</summary>
    private async Task<bool> ConfirmOverwriteAsync(string reason)
    {
        var dlg = new ContentDialog
        {
            Title = "覆盖安装确认",
            Content = new TextBlock
            {
                Text = $"{reason}\n是否覆盖安装？",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "覆盖安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>把 PluginInstallResult 映射为中文反馈文本（含回滚提示）。</summary>
    private void ApplyInstallResult(PluginInstallResult result)
    {
        var suffix = result.RolledBack ? "（已回滚至旧版本）" : "";
        bool ok;
        string message;
        switch (result.Status)
        {
            case PluginInstallStatus.Installed:
                ok = true;
                var version = result.NewVersion ?? result.Plugin?.Version ?? "";
                message = string.IsNullOrEmpty(result.Plugin?.Name)
                    ? $"安装成功 v{version}"
                    : $"安装成功：{result.Plugin.Name} v{version}";
                break;
            case PluginInstallStatus.Upgraded:
                ok = true;
                message = $"已从 v{result.OldVersion} 升级到 v{result.NewVersion}";
                break;
            case PluginInstallStatus.Overwritten:
                ok = true;
                message = $"已覆盖安装 v{result.NewVersion}";
                break;
            case PluginInstallStatus.InvalidPackage:
                ok = false;
                message = $"安装包无效：{result.Error}{suffix}";
                break;
            case PluginInstallStatus.Incompatible:
                ok = false;
                message = $"版本不兼容：{result.Error}{suffix}";
                break;
            case PluginInstallStatus.IdConflict:
                ok = false;
                message = $"{result.Error}{suffix}";
                break;
            default: // Failed
                ok = false;
                message = $"安装失败：{result.Error}{suffix}";
                break;
        }

        SetStatus(message, isError: !ok);
        if (ok) Logger.Info($"插件安装结果: {message}");
        else Logger.Warn($"插件安装结果: {message}");
    }

    // ============== 卸载 ==============

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: PluginInfo plugin }) return;
        if (PluginInstaller.IsBuiltIn(plugin.Directory)) return; // 双保险（按钮已置灰）

        var removeDataCheck = new CheckBox { Content = "同时删除插件数据" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = $"确定要卸载「{plugin.Name}」吗？插件文件将被移入回收目录并删除。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(removeDataCheck);

        var dlg = new ContentDialog
        {
            Title = "卸载插件",
            Content = panel,
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true);
        try
        {
            var removeData = removeDataCheck.IsChecked == true;
            var result = await _installer.UninstallAsync(plugin.Id, removeData);
            if (result.Success)
            {
                SetStatus($"已卸载 {plugin.Name}（插件数据{(removeData ? "已删除" : "已保留")}）");
                Logger.Info($"插件管理窗口: 已卸载 {plugin.Id}（数据{(removeData ? "删除" : "保留")}）");
            }
            else
            {
                SetStatus($"卸载失败：{result.Error}", isError: true);
                Logger.Warn($"卸载插件失败 {plugin.Id}: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"卸载失败：{ex.Message}", isError: true);
            Logger.Warn($"卸载插件异常 {plugin.Id}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            RefreshList();
        }
    }

    // ============== 重扫 / 打开目录 ==============

    private async void RescanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var added = await _manager.RescanAsync();
            SetStatus(added > 0 ? $"扫描完成：新加载 {added} 个" : "扫描完成：没有发现新插件");
            Logger.Info($"插件管理窗口: 重扫完成，新增 {added} 个");
        }
        catch (Exception ex)
        {
            SetStatus($"扫描失败：{ex.Message}", isError: true);
            Logger.Warn($"重新扫描插件失败: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            RefreshList();
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            Directory.CreateDirectory(PluginManager.UserPluginsRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{PluginManager.UserPluginsRoot}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"打开插件目录失败：{ex.Message}", isError: true);
        }
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PluginInfo plugin }) return;
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SMTCPlayer", "plugins-data", plugin.Id);
            if (Directory.Exists(dataDir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dataDir}\"") { UseShellExecute = true });
            else
                SetStatus($"插件数据目录不存在（尚未产生数据）：{dataDir}");
        }
        catch (Exception ex)
        {
            SetStatus($"打开数据目录失败：{ex.Message}", isError: true);
        }
    }

    private void OpenPluginFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PluginInfo plugin }) return;
        try
        {
            if (Directory.Exists(plugin.Directory))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{plugin.Directory}\"") { UseShellExecute = true });
            else
                SetStatus($"插件目录不存在：{plugin.Directory}", isError: true);
        }
        catch (Exception ex)
        {
            SetStatus($"打开插件目录失败：{ex.Message}", isError: true);
        }
    }
}
