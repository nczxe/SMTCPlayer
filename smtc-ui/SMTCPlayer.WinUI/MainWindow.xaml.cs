using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using H.NotifyIcon;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using SMTCPlayer.Core.Services;
using SMTCPlayer.Core.ViewModels;
using SMTCPlayer.WinUI.Dialogs;
using SMTCPlayer.WinUI.Services;
using Windows.Storage.Streams;

namespace SMTCPlayer.WinUI;

/// <summary>
/// 主窗口。负责 Mica 背景启用、ViewModel 绑定、横竖屏自适应、
/// 二维码生成、PIN 流程、托盘图标与服务生命周期管理。
/// </summary>
public sealed partial class MainWindow : Window
{
    private MainViewModel _vm = null!;
    private FlaskServerManager? _server;
    private DispatcherQueue _dispatcher = null!;
    private H.NotifyIcon.TaskbarIcon? _trayIcon;
    private bool _authenticating;
    private bool _forceExit;        // 托盘退出/确认退出后置位，放行真正的窗口关闭
    private bool _cleanupDone;      // 退出清理只执行一次
    private bool _closeDialogShowing;

    public MainViewModel Vm => _vm;

    public MainWindow()
    {
        InitializeComponent();

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Title = "SMTC Player";

        // 加载持久化设置
        AppSettings.Load();
        RootGrid.RequestedTheme = AppSettings.GetTheme();
        if (AppSettings.GetDebugMode())
            Logger.MinLevel = LogLevel.Debug;
        else
            Logger.MinLevel = LogLevel.Info;

        TryEnableMicaBackdrop();
        InitializeViewModel();
        SetupTrayIcon();
        UpdateAlbumVisibility();

        this.SizeChanged += OnSizeChanged;
        this.Closed += OnClosed;
        // 拦截标题栏关闭按钮：询问退出/最小化到托盘（AppWindow.Closing 可取消）
        this.AppWindow.Closing += OnAppWindowClosing;
    }

    // ============== Mica 背景 ==============

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

    // ============== 专辑名显示（设置开关控制） ==============

    private void UpdateAlbumVisibility()
    {
        var visibility = AppSettings.GetShowAlbum() ? Visibility.Visible : Visibility.Collapsed;
        AlbumTextPortrait.Visibility = visibility;
        AlbumTextLandscape.Visibility = visibility;
    }

    // ============== ViewModel 初始化 ==============

    private void InitializeViewModel()
    {
        var pythonPath = FindPython() ?? "python";
        var serverDir = FindServerDir();
        var port = 8888;

        var apiClient = new SmtcApiClient("127.0.0.1", port);
        _server = new FlaskServerManager(pythonPath, serverDir);
        _vm = new MainViewModel(apiClient, _server, port, new WinUIClipboardService());
        _vm.PropertyChanged += OnViewModelChanged;
        _vm.UpdateAvailable += OnUpdateAvailable;
        _vm.PositionOffsetSeconds = AppSettings.GetPositionOffsetMs() / 1000.0;

        // 全窗口设置面板事件
        SettingsOverlay.StateChanged += () =>
        {
            RootGrid.RequestedTheme = SettingsOverlay.SelectedTheme;
            UpdateAlbumVisibility();
        };
        SettingsOverlay.CloseRequested += () => SettingsOverlay.Visibility = Visibility.Collapsed;
        SettingsOverlay.PinRequested += () => _ = HandleSettingsPinRequestAsync();
        SettingsOverlay.AboutRequested += () => _ = ShowAboutDialogAsync();
        SettingsOverlay.PositionOffsetRequested += () => _ = ShowPositionOffsetDialogAsync();

        UpdateQrCode($"http://{FlaskServerManager.GetLocalIp()}:{_vm.Port}");
        UpdateVolumeIcon();
        UpdatePlayPauseIcon();
    }

    private static string? FindPython()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "Python", "Python314", "python.exe"),
            Path.Combine(localAppData, "Programs", "Python", "Python313", "python.exe"),
            Path.Combine(localAppData, "Programs", "Python", "Python312", "python.exe"),
            Path.Combine(localAppData, "Programs", "Python", "Python311", "python.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    private static string FindServerDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "server"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "server")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "..", "server")),
        };
        foreach (var c in candidates)
            if (Directory.Exists(Path.Combine(c, "static")) || File.Exists(Path.Combine(c, "app.py")))
                return c;
        return candidates[0];
    }

    // ============== ViewModel 事件 ==============

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        _dispatcher.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.IsServerRunning):
                    UpdateViewVisibility();
                    if (_vm.IsServerRunning && !_authenticating)
                        _ = AuthenticateAfterStartAsync();
                    break;
                case nameof(MainViewModel.LanUrl):
                    UpdateQrCode(_vm.LanUrl);
                    break;
                case nameof(MainViewModel.IsMuted):
                case nameof(MainViewModel.VolumeValue):
                case nameof(MainViewModel.VolumeText):
                    UpdateVolumeIcon();
                    break;
                case nameof(MainViewModel.IsPlaying):
                    UpdatePlayPauseIcon();
                    break;
                case nameof(MainViewModel.ThumbnailUrl):
                    Logger.Debug($"ThumbnailUrl 变更: {_vm.ThumbnailUrl ?? "(null)"}");
                    UpdateCoverImage();
                    break;
            }
        });
    }

    // ============== 视图切换 ==============

    private void UpdateViewVisibility()
    {
        if (_vm.IsServerRunning)
        {
            // 服务运行：隐藏启动视图，根据宽度切到播放视图
            Logger.Debug("UpdateViewVisibility: 服务运行中，切换到播放视图");
            StartView.Visibility = Visibility.Collapsed;
            ApplyAdaptiveLayout();
        }
        else
        {
            // 服务停止：只显示启动视图，隐藏所有播放视图
            Logger.Info("UpdateViewVisibility: 服务已停止，回到启动视图");
            StartView.Visibility = Visibility.Visible;
            PortraitView.Visibility = Visibility.Collapsed;
            LandscapeView.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyAdaptiveLayout()
    {
        if (!_vm.IsServerRunning) return;

        var width = this.Bounds.Width;
        if (width >= 720)
        {
            Logger.Debug($"ApplyAdaptiveLayout: width={width:F0}px → 横屏视图");
            PortraitView.Visibility = Visibility.Collapsed;
            LandscapeView.Visibility = Visibility.Visible;
        }
        else
        {
            Logger.Debug($"ApplyAdaptiveLayout: width={width:F0}px → 竖屏视图");
            PortraitView.Visibility = Visibility.Visible;
            LandscapeView.Visibility = Visibility.Collapsed;
        }
    }

    // ============== 二维码 ==============

    private void UpdateQrCode(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            using var generator = new QRCodeGenerator();
            var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.L);
            var qrCode = new PngByteQRCode(data);
            var bytes = qrCode.GetGraphic(10);

            _ = ApplyQrImageAsync(bytes);
        }
        catch (Exception ex)
        {
            Logger.Warn($"二维码生成失败：{ex.Message}");
        }
    }

    private async Task ApplyQrImageAsync(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using var dataWriter = new DataWriter(stream);
            dataWriter.WriteBytes(bytes);
            await dataWriter.StoreAsync();
            await stream.FlushAsync();
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            QrImagePortrait.Source = bitmap;
            QrImageLandscape.Source = bitmap;
        }
        catch (Exception ex)
        {
            Logger.Warn($"二维码渲染失败：{ex.Message}");
        }
    }

    // ============== 图标更新 ==============

    private void UpdateCoverImage()
    {
        try
        {
            var url = _vm.ThumbnailUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                CoverImagePortrait.Source = null;
                CoverImageLandscape.Source = null;
                Logger.Debug("UpdateCoverImage: URL 为空，清除封面");
                return;
            }

            Logger.Info($"UpdateCoverImage: 加载封面: {url}");
            var bitmap = new BitmapImage
            {
                UriSource = new Uri(url),
                CreateOptions = BitmapCreateOptions.IgnoreImageCache,
            };
            CoverImagePortrait.Source = bitmap;
            CoverImageLandscape.Source = bitmap;
        }
        catch (Exception ex)
        {
            Logger.Warn($"UpdateCoverImage 失败: {ex.Message}");
        }
    }

    private string? _lastVolumeGlyph;

    private void UpdateVolumeIcon()
    {
        // U+E74F Mute, U+E992 VolumeLow, U+E767 Volume
        string glyph;
        if (_vm.IsMuted || _vm.VolumeValue <= 0)
            glyph = "\uE74F";
        else if (_vm.VolumeValue < 30)
            glyph = "\uE992";
        else
            glyph = "\uE767";

        if (glyph == _lastVolumeGlyph) return; // 状态未变：跳过重复更新与日志
        _lastVolumeGlyph = glyph;

        Logger.Debug($"UpdateVolumeIcon: IsMuted={_vm.IsMuted}, Volume={_vm.VolumeValue}, glyph={glyph}");
        VolumeIconPortrait.Glyph = glyph;
        VolumeIconLandscape.Glyph = glyph;
    }

    private void UpdatePlayPauseIcon()
    {
        // U+E768 Play, U+E769 Pause
        var glyph = _vm.IsPlaying ? "\uE769" : "\uE768";
        PlayPauseIconPortrait.Glyph = glyph;
        PlayPauseIconLandscape.Glyph = glyph;
    }

    // ============== PIN 设置流程 ==============

    private async Task AuthenticateAfterStartAsync()
    {
        _authenticating = true;
        try
        {
            // 重试获取服务端 auth 状态（最多 10 次，每次 500ms）
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    var status = await _vm.GetAuthStatusAsync();
                    if (status != null) break;
                }
                catch { /* 服务端尚未就绪 */ }
                await Task.Delay(500);
            }

            // 如果服务端还没设置 PIN，弹出设置对话框
            try
            {
                var auth = await _vm.GetAuthStatusAsync();
                // Configured=false 表示后端还没设置过 PIN，需要初始化
                if (auth is { Configured: false })
                {
                    await OpenPinSetupDialogAsync(resetMode: true);
                }
            }
            catch { /* 忽略 */ }
        }
        finally
        {
            _authenticating = false;
        }
    }

    private async Task OpenPinSetupDialogAsync(bool resetMode)
    {
        Logger.Info($"OpenPinSetupDialogAsync: 开始，resetMode={resetMode}");
        bool success = false;
        while (!success)
        {
            var dialog = new PinSetupDialog();
            dialog.XamlRoot = ContentRoot.XamlRoot;
            if (resetMode)
                dialog.UseInitialSetupMode();
            else
                dialog.UseChangeMode();

            var result = await dialog.ShowAsync();
            Logger.Info($"OpenPinSetupDialogAsync: 用户结果={result}");

            if (result != ContentDialogResult.Primary) break;

            try
            {
                // 服务端 reset_pin 无需旧 PIN，统一用 ResetPinAsync
                var resp = await _vm.ResetPinAsync(dialog.Pin);

                if (resp?.Success == true)
                {
                    success = true;
                    await ShowInfoAsync(resetMode ? "PIN 设置成功" : "PIN 修改成功");
                }
                else
                {
                    await ShowErrorAsync($"操作失败：{resp?.Error ?? "未知错误"}");
                }
            }
            catch (Exception ex)
            {
                await ShowErrorAsync($"操作异常：{ex.Message}");
            }
        }
    }

    // ============== 设置按钮 ==============

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.LoadState(RootGrid.RequestedTheme, _vm.PluginHost);
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>设置面板请求修改 PIN（原 ContentDialog 循环逻辑的等价迁移）。</summary>
    private async Task HandleSettingsPinRequestAsync()
    {
        if (!_vm.IsServerRunning)
        {
            await ShowInfoAsync("请先启动服务，再修改访问 PIN。");
            return;
        }
        await OpenPinSetupDialogAsync(resetMode: false);
    }

    // ============== 播放进度校准 ==============

    /// <summary>
    /// 打开进度偏移校准对话框：调整期间实时写入 ViewModel 预览；
    /// 保存则持久化到 AppSettings，取消则回滚到打开前的值。
    /// </summary>
    private async Task ShowPositionOffsetDialogAsync()
    {
        var original = AppSettings.GetPositionOffsetMs();
        _vm.PositionOffsetSeconds = original / 1000.0;

        var dialog = new PositionOffsetDialog(_vm, original)
        {
            XamlRoot = ContentRoot.XamlRoot,
            OffsetChanged = ms => _vm.PositionOffsetSeconds = ms / 1000.0,
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            AppSettings.SetPositionOffsetMs(dialog.OffsetMs);
            _vm.PositionOffsetSeconds = dialog.OffsetMs / 1000.0;
            SettingsOverlay.UpdatePositionOffsetText(dialog.OffsetMs);
            Logger.Info($"播放进度偏移已保存: {dialog.OffsetMs} ms");
        }
        else
        {
            _vm.PositionOffsetSeconds = original / 1000.0;
        }
    }

    // ============== 通用 Dialog ==============

    private async Task ShowErrorAsync(string msg)
    {
        var dlg = new ContentDialog
        {
            Title = "出错了",
            Content = msg,
            CloseButtonText = "知道了",
            XamlRoot = ContentRoot.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    private async Task ShowInfoAsync(string msg)
    {
        var dlg = new ContentDialog
        {
            Title = "提示",
            Content = msg,
            CloseButtonText = "好的",
            XamlRoot = ContentRoot.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    /// <summary>启动静默检查发现新版本时弹出提醒（仅引导前往发布页，不自动下载）。</summary>
    private async void OnUpdateAvailable(UpdateInfo info)
    {
        try
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = $"发现新版本 v{info.LatestVersion}（当前 v{info.CurrentVersion}）",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock
            {
                Text = "请前往 GitHub 发布页手动下载安装。",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });

            var dlg = new ContentDialog
            {
                Title = "有可用更新",
                Content = panel,
                PrimaryButtonText = "打开发布页",
                CloseButtonText = "忽略",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = ContentRoot.XamlRoot,
            };
            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                Process.Start(new ProcessStartInfo(info.ReleaseUrl) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"显示更新提醒失败: {ex.Message}");
        }
    }

    private async Task ShowAboutDialogAsync()
    {
        // 用 C# 代码构建关于对话框，避免 XamlCompiler 崩溃
        var panel = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(24, 32, 24, 24) };

        var icon = new FontIcon { Glyph = "\uE8B6", FontSize = 48, HorizontalAlignment = HorizontalAlignment.Center };
        icon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

        var title = new TextBlock { Text = "SMTCPlayer", FontSize = 42, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        // 版本号读程序集 InformationalVersion（形如 1.1.1+26082323500S.<commit>），与 csproj 单一来源
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string versionText;
        if (string.IsNullOrEmpty(informational))
        {
            versionText = "WinUI版本 v0.0.0";
        }
        else
        {
            // "1.1.1+26082323500S.abc123" → "v1.1.1 (build 26082323500S)"，丢弃构建工具追加的 commit 段
            var segs = informational.Split('+');
            var build = segs.Length > 1 ? segs[1].Split('.')[0] : "";
            versionText = $"WinUI版本 v{segs[0]}" + (build.Length > 0 ? $" (build {build})" : "");
        }
        var version = new TextBlock { Text = versionText, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center };
        version.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        var author = new TextBlock { Text = "by FR-NEXT", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
        author.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

        var separator = new Border { Height = 1, Width = 200, Margin = new Thickness(0, 4, 0, 0) };
        separator.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

        var link = new HyperlinkButton { Content = "splay.asia", NavigateUri = new Uri("https://splay.asia"), FontSize = 13, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center };

        panel.Children.Add(icon);
        panel.Children.Add(title);
        panel.Children.Add(version);
        panel.Children.Add(author);
        panel.Children.Add(separator);
        panel.Children.Add(link);

        var dlg = new ContentDialog
        {
            Title = "关于",
            Content = panel,
            CloseButtonText = "关闭",
            XamlRoot = ContentRoot.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    // ============== 事件处理 ==============

    private void OnSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        ApplyAdaptiveLayout();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Debug("MuteButton_Click: 切换静音（先本地翻转，再发 API）");
        _vm.ToggleMuteCommand.Execute(null);
        UpdateVolumeIcon();
    }

    private void Url_Click(object sender, RoutedEventArgs e)
    {
        Logger.Debug("Url_Click: 复制 URL");
        _vm.CopyUrlCommand.Execute(null);
    }

    private void StopServer_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("StopServer_Click: 用户请求停止服务");
        try
        {
            _vm.StopPolling();
            _server?.Stop();
        }
        catch (Exception ex)
        {
            Logger.Warn($"停止服务异常：{ex.Message}");
        }
        finally
        {
            _vm.IsServerRunning = false;
        }
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Logger.Debug("MinimizeToTray_Click");
        MinimizeToTray();
    }

    private void MinimizeToTray()
    {
        Logger.Info("隐藏到托盘");
        try { this.Hide(enableEfficiencyMode: true); }
        catch (Exception ex) { Logger.Warn($"Hide 失败: {ex.Message}"); }
    }

    /// <summary>从托盘恢复窗口显示。</summary>
    private void RestoreFromTray()
    {
        Logger.Info("从托盘恢复窗口");
        try { this.Show(disableEfficiencyMode: true); }
        catch (Exception ex) { Logger.Warn($"Show 失败: {ex.Message}"); }
    }

    /// <summary>第二次启动实例触发：唤起既有实例并置前。</summary>
    internal void BringToFrontFromExternalLaunch()
    {
        RestoreFromTray();
        try { this.Activate(); } catch { /* 已激活时忽略 */ }
    }

    // ============== 托盘图标（含右键菜单） ==============

    private void SetupTrayIcon()
    {
        try
        {
            Logger.Info("开始创建托盘图标...");

            _trayIcon = new H.NotifyIcon.TaskbarIcon
            {
                ToolTipText = "SMTC Player",
                IconSource = new H.NotifyIcon.GeneratedIconSource
                {
                    Text = "♪",
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                },
                LeftClickCommand = new RelayCommand(_ => ToggleWindowFromTray()),
                RightClickCommand = new RelayCommand(_ => ShowTrayContextMenu()),
                NoLeftClickDelay = true,
                ContextMenuMode = ContextMenuMode.ActiveWindow,
            };

            _trayIcon.ForceCreate();
            Logger.Info($"托盘图标创建成功，IsCreated={_trayIcon.IsCreated}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"托盘图标初始化失败：{ex}");
        }
    }

    // ============== 托盘右键菜单 ==============

    private MenuFlyout BuildTrayContextMenu()
    {
        var flyout = new MenuFlyout();

        var restore = new MenuFlyoutItem { Text = "显示主窗口" };
        restore.Icon = new FontIcon { Glyph = "\uE737" };
        restore.Click += TrayMenu_Restore_Click;
        flyout.Items.Add(restore);

        var hide = new MenuFlyoutItem { Text = "隐藏到托盘" };
        hide.Icon = new FontIcon { Glyph = "\uE949" };
        hide.Click += TrayMenu_Hide_Click;
        flyout.Items.Add(hide);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var settings = new MenuFlyoutItem { Text = "设置" };
        settings.Icon = new FontIcon { Glyph = "\uE713" };
        settings.Click += TrayMenu_Settings_Click;
        flyout.Items.Add(settings);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var exit = new MenuFlyoutItem { Text = "退出应用" };
        exit.Icon = new FontIcon { Glyph = "\uE8BB" };
        exit.Click += TrayMenu_Exit_Click;
        flyout.Items.Add(exit);

        return flyout;
    }

    private void ShowTrayContextMenu()
    {
        Logger.Debug("ShowTrayContextMenu: 托盘右键被触发");
        try
        {
            _dispatcher.TryEnqueue(() =>
            {
                // WinUI 3 的 Flyout.ShowAt 需要有效的 XamlRoot，所以先确保窗口可见
                if (!this.AppWindow.IsVisible) RestoreFromTray();

                TrayMenuAnchor.Visibility = Visibility.Visible;
                TrayMenuAnchor.HorizontalAlignment = HorizontalAlignment.Left;
                TrayMenuAnchor.VerticalAlignment = VerticalAlignment.Bottom;
                var flyout = BuildTrayContextMenu();
                flyout.ShowAt(TrayMenuAnchor);
                Logger.Debug("ShowTrayContextMenu: 菜单已显示");
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"显示托盘菜单失败：{ex.Message}");
        }
    }

    private void TrayMenu_Restore_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("托盘菜单: 显示主窗口");
        RestoreFromTray();
    }
    private void TrayMenu_Hide_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("托盘菜单: 隐藏到托盘");
        MinimizeToTray();
    }
    private async void TrayMenu_Settings_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("托盘菜单: 设置 PIN");
        RestoreFromTray();
        await Task.Delay(100);
        if (_vm.IsServerRunning)
            await OpenPinSetupDialogAsync(resetMode: false);
        else
            Logger.Warn("托盘菜单: 服务未运行，跳过 PIN 设置");
    }
    private async void TrayMenu_Exit_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("托盘菜单: 退出应用");
        _forceExit = true;
        await PerformExitCleanupAsync();
        _dispatcher.TryEnqueue(Close);
    }

    private void ToggleWindowFromTray()
    {
        try
        {
            if (this.AppWindow.IsVisible)
            {
                Logger.Debug("托盘左键: 当前可见，隐藏");
                MinimizeToTray();
            }
            else
            {
                Logger.Debug("托盘左键: 当前隐藏，显示");
                RestoreFromTray();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"切换窗口显示异常：{ex.Message}");
        }
    }

    // ============== 窗口关闭 ==============

    /// <summary>
    /// 拦截标题栏关闭按钮：询问用户"退出 / 最小化到托盘"，可记住选择。
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_forceExit)
        {
            Logger.Info("窗口关闭：清理已完成的强制退出，放行");
            return;
        }

        e.Cancel = true;
        if (_closeDialogShowing) return;
        _closeDialogShowing = true;
        _dispatcher.TryEnqueue(() => _ = HandleCloseRequestAsync());
    }

    private async Task HandleCloseRequestAsync()
    {
        try
        {
            var remembered = AppSettings.GetCloseAction();
            string choice;

            if (remembered == "minimize" || remembered == "exit")
            {
                choice = remembered;
                Logger.Info($"关闭行为使用记住的选择: {choice}");
            }
            else
            {
                var result = await ShowCloseChoiceDialogAsync();
                if (result == null) return; // 用户取消，什么都不做
                choice = result.Value.Choice;
                if (result.Value.Remember) AppSettings.SetCloseAction(choice);
            }

            if (choice == "minimize")
            {
                MinimizeToTray();
            }
            else
            {
                await PerformExitCleanupAsync();
                _forceExit = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"关闭请求处理异常: {ex.Message}");
        }
        finally
        {
            _closeDialogShowing = false;
        }
    }

    /// <summary>返回 (Choice, Remember)；用户取消时返回 null。默认选中"最小化到托盘"。</summary>
    private async Task<(string Choice, bool Remember)?> ShowCloseChoiceDialogAsync()
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = "您点击了关闭按钮，请问要做什么？",
            TextWrapping = TextWrapping.Wrap,
        });

        var options = new RadioButtons();
        options.Items.Add(new RadioButton { Content = "退出 SMTCPlayer" });
        options.Items.Add(new RadioButton { Content = "最小化到托盘运行", IsChecked = true });
        panel.Children.Add(options);

        panel.Children.Add(new CheckBox { Content = "记住我的选择（可在设置中更改）" });

        var dlg = new ContentDialog
        {
            Title = "关闭 SMTC Player",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = ContentRoot.XamlRoot,
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;
        return (options.SelectedIndex == 0 ? "exit" : "minimize",
                (panel.Children[2] as CheckBox)?.IsChecked == true);
    }

    /// <summary>
    /// 退出前清理：全部放到后台线程执行并限时等待，
    /// 修复此前在 UI 线程同步等待 ShutdownPlugins/Stop 导致的关窗卡死。
    /// </summary>
    private async Task PerformExitCleanupAsync()
    {
        if (_cleanupDone) return;
        _cleanupDone = true;
        Logger.Info("=== 开始退出清理（后台线程） ===");

        try
        {
            await Task.Run(async () =>
            {
                try { _vm.StopPolling(); } catch { }
                try { await _vm.ShutdownPluginsAsync(); } catch (Exception ex) { Logger.Warn($"插件清理异常: {ex.Message}"); }
                try { _server?.Stop(); } catch (Exception ex) { Logger.Warn($"服务停止异常: {ex.Message}"); }
            }).WaitAsync(TimeSpan.FromSeconds(8)); // 总兜底超时，绝不无限等待
        }
        catch (Exception ex)
        {
            Logger.Warn($"退出清理超时或异常: {ex.Message}");
        }

        try { _trayIcon?.Dispose(); } catch { }
        Logger.Info("退出清理完成");
    }

    /// <summary>窗口真正关闭后的收尾：仅幂等轻量操作，不再阻塞。</summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        Logger.Info("=== 主窗口已关闭 ===");
        if (_cleanupDone) return;

        // 非常规路径（系统强制等）：后台补一次清理，不阻塞 UI 线程
        _cleanupDone = true;
        Task.Run(async () =>
        {
            try
            {
                _vm.StopPolling();
                await _vm.ShutdownPluginsAsync();
                _server?.Stop();
            }
            catch { }
            try { _trayIcon?.Dispose(); } catch { }
        });
    }
}
