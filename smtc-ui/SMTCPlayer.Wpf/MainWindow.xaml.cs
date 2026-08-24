using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using QRCoder;
using SMTCPlayer.Core.Models;
using SMTCPlayer.Core.Services;
using SMTCPlayer.Core.ViewModels;
using SMTCPlayer.Wpf.Services;
using System.Drawing;

namespace SMTCPlayer.Wpf;

public partial class MainWindow : Window
{
    private readonly FlaskServerManager? _server;
    private readonly MainViewModel _vm;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private SettingsWindow? _settingsWindow;
    private bool _forceExit;
    private bool _isLandscape;

    public MainWindow()
    {
        Logger.Info("MainWindow 构造开始");

        var pythonPath = FindPython() ?? "python";
        var serverDir = FindServerDir();
        Logger.Info($"FindPython => {pythonPath}");
        Logger.Info($"FindServerDir => {serverDir}");

        var port = Properties.Settings.Default.Port > 0 ? Properties.Settings.Default.Port : 8888;
        var apiClient = new SmtcApiClient("127.0.0.1", port);
        _server = new FlaskServerManager(pythonPath, serverDir);
        _vm = new MainViewModel(apiClient, _server, port, new WpfClipboardService());
        _vm.PropertyChanged += OnViewModelChanged;

        DataContext = _vm;
        InitializeComponent();

        GenerateQrCode($"http://{FlaskServerManager.GetLocalIp()}:{_vm.Port}");

        Closing += (_, e) =>
        {
            if (!_forceExit && Properties.Settings.Default.MinimizeOnClose)
            {
                e.Cancel = true;
                EnsureNotifyIcon();
                Hide();
                return;
            }

            if (!_forceExit)
            {
                var result = System.Windows.MessageBox.Show(
                    "退出 SMTC Player？", "SMTC Player",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            Logger.Info("MainWindow 正在关闭");
            _vm.StopPolling();
            try { _vm.ShutdownPlugins(); }
            catch (Exception ex) { Logger.Warn($"插件关闭异常: {ex.Message}"); }
            _server?.Stop();
            _notifyIcon?.Dispose();
        };

        Loaded += (_, _) =>
        {
            ApplyFadeInAnimation(StartGrid);
            UpdateOrientation();
            if (Properties.Settings.Default.MinimizeOnStart)
            {
                Hide();
                EnsureNotifyIcon();
            }
            if (Properties.Settings.Default.DebugMode)
            {
                DebugWindow.Open(this);
            }
        };
        Logger.Info("MainWindow 构造完成");
    }

    private void ApplyFadeInAnimation(FrameworkElement element)
    {
        element.RenderTransform = new System.Windows.Media.TranslateTransform(0, 16);
        var storyboard = (Storyboard)FindResource("FadeIn");
        storyboard.Begin(element);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LanUrl))
        {
            Dispatcher.Invoke(() => GenerateQrCode(_vm.LanUrl));
        }
        if (e.PropertyName == nameof(MainViewModel.IsPlaying))
        {
            Dispatcher.Invoke(() =>
            {
                var content = _vm.IsPlaying ? "⏸" : "▶";
                if (P_PlayPauseBtn != null) P_PlayPauseBtn.Content = content;
                if (L_PlayPauseBtn != null) L_PlayPauseBtn.Content = content;
            });
        }
        if (e.PropertyName == nameof(MainViewModel.VolumeValue) || e.PropertyName == nameof(MainViewModel.IsMuted))
        {
            Dispatcher.Invoke(UpdateVolumeIcon);
        }
        if (e.PropertyName == nameof(MainViewModel.IsServerRunning))
        {
            Dispatcher.Invoke(() =>
            {
                if (_vm.IsServerRunning)
                {
                    StartGrid.Visibility = Visibility.Collapsed;
                    SwitchToCurrentOrientation();
                    ApplyFadeInAnimation(_isLandscape ? LandscapeGrid : PortraitGrid);
                    UpdateElementSizes();
                    _ = AuthenticateAsync();
                }
                else
                {
                    PortraitGrid.Visibility = Visibility.Collapsed;
                    LandscapeGrid.Visibility = Visibility.Collapsed;
                    StartGrid.Visibility = Visibility.Visible;
                }
            });
        }
        if (e.PropertyName == nameof(MainViewModel.ThumbnailUrl))
        {
            Dispatcher.Invoke(() => LoadThumbnail(_vm.ThumbnailUrl));
        }
    }

    private void GenerateQrCode(string url)
    {
        using var qrGen = new QRCodeGenerator();
        using var qrData = qrGen.CreateQrCode(url, QRCodeGenerator.ECCLevel.L);
        using var qrCode = new PngByteQRCode(qrData);
        var bytes = qrCode.GetGraphic(10, new byte[] { 139, 92, 246 }, new byte[] { 22, 26, 34 });
        using var ms = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.StreamSource = ms;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        if (P_QrImage != null) P_QrImage.Source = bitmap;
        if (L_QrImage != null) L_QrImage.Source = bitmap;
    }

    private async void LoadThumbnail(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            if (P_AlbumImage != null) P_AlbumImage.Source = null;
            if (L_AlbumImage != null) L_AlbumImage.Source = null;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(url, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            if (P_AlbumImage != null) P_AlbumImage.Source = bitmap;
            if (L_AlbumImage != null) L_AlbumImage.Source = bitmap;
        }
        catch
        {
            if (P_AlbumImage != null) P_AlbumImage.Source = null;
            if (L_AlbumImage != null) L_AlbumImage.Source = null;
        }
    }

    private void UpdateVolumeIcon()
    {
        string icon;
        if (_vm.IsMuted || _vm.VolumeValue <= 0)
            icon = "🔇";
        else if (_vm.VolumeValue < 50)
            icon = "🔉";
        else
            icon = "🔊";
        if (P_VolumeIcon != null) P_VolumeIcon.Content = icon;
        if (L_VolumeIcon != null) L_VolumeIcon.Content = icon;
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e) => _vm.CopyUrlCommand.Execute(null);

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{_vm.Port}") { UseShellExecute = true });
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        EnsureNotifyIcon();
    }

    private void EnsureNotifyIcon()
    {
        if (_notifyIcon != null)
        {
            try { _notifyIcon.Visible = true; } catch { }
            return;
        }

        try
        {
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!);
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "SMTC Player",
                Icon = icon,
                Visible = true,
                ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
            };
        }
        catch (Exception ex)
        {
            Logger.Warn($"创建托盘图标失败: {ex.Message}");
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "SMTC Player",
                Visible = false,
                ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
            };
        }

        var showItem = new System.Windows.Forms.ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => { Show(); Activate(); _notifyIcon.Visible = false; };
        _notifyIcon.ContextMenuStrip.Items.Add(showItem);

        _notifyIcon.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var quitItem = new System.Windows.Forms.ToolStripMenuItem("退出");
        quitItem.Click += (_, _) =>
        {
            _notifyIcon.Visible = false;
            Quit_Click(this, new RoutedEventArgs());
        };
        _notifyIcon.ContextMenuStrip.Items.Add(quitItem);

        _notifyIcon.DoubleClick += (_, _) => { Show(); Activate(); _notifyIcon.Visible = false; };
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_vm.Port);
            _settingsWindow.Owner = this;
            _settingsWindow.ExitRequested += () =>
            {
                _settingsWindow?.Close();
                Quit_Click(this, new RoutedEventArgs());
            };
            _settingsWindow.SettingsSaved += port =>
            {
                if (port > 0 && port < 65536)
                    _vm.Port = port;
            };
            _settingsWindow.ShowDialog();
        }
        else
        {
            _settingsWindow.Activate();
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var about = new Window
        {
            Title = "关于",
            Width = 340,
            Height = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = FindResource("BgPrimaryBrush") as System.Windows.Media.Brush,
            ResizeMode = ResizeMode.NoResize,
        };

        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        var icon = new System.Windows.Controls.TextBlock
        {
            Text = "♫",
            FontSize = 40,
            Foreground = FindResource("AccentBrush") as System.Windows.Media.Brush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        System.Windows.Controls.Grid.SetRow(icon, 0);
        grid.Children.Add(icon);

        var title = new System.Windows.Controls.TextBlock
        {
            Text = "SMTC Player",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = FindResource("TextPrimaryBrush") as System.Windows.Media.Brush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 4),
        };
        System.Windows.Controls.Grid.SetRow(title, 1);
        grid.Children.Add(title);

        // 版本号读程序集，与 csproj <Version> 单一来源
        var asmVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        var versionText = asmVersion != null ? $"v{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}" : "v0.0.0";
        var version = new System.Windows.Controls.TextBlock
        {
            Text = $"{versionText} - FR-NEXT",
            FontSize = 11,
            Foreground = FindResource("TextMutedBrush") as System.Windows.Media.Brush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
        };
        System.Windows.Controls.Grid.SetRow(version, 2);
        grid.Children.Add(version);

        var desc = new System.Windows.Controls.TextBlock
        {
            Text = "远程控制 Windows 媒体播放\n支持SMTC应用的播放控制",
            FontSize = 11,
            Foreground = FindResource("TextSecondaryBrush") as System.Windows.Media.Brush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        System.Windows.Controls.Grid.SetRow(desc, 3);
        grid.Children.Add(desc);

        about.Content = grid;
        about.ShowDialog();
    }

    private async Task AuthenticateAsync()
    {
        try
        {
            var status = await _vm.GetAuthStatusAsync();
            for (int i = 0; i < 10 && status == null; i++)
            {
                await Task.Delay(500);
                status = await _vm.GetAuthStatusAsync();
            }
            if (status == null)
            {
                Logger.Warn("无法获取认证状态，跳过 PIN 设置流程");
                return;
            }

            string? error = null;
            while (true)
            {
                // 按设计：WPF 是本机客户端，始终走 reset_pin 接口（仅允许 127.0.0.1 / ::1）
                // 不区分是否已配置，不要求旧 PIN，直接以"设置初始PIN"对话框方式弹
                var dialog = new AuthWindow(isSetup: true) { Owner = this };
                if (error != null)
                    dialog.ShowError(error);
                if (dialog.ShowDialog() != true)
                {
                    Logger.Info("用户取消 PIN 设置，继续以本机模式运行（WPF 访问本机 API 无需 PIN）");
                    return;
                }

                var result = await _vm.ResetPinAsync(dialog.Pin);
                if (result?.Success == true)
                {
                    Logger.Info("初始 PIN 设置成功");
                    return;
                }
                error = result?.Error ?? "PIN 无法设置，请重试";
                Logger.Warn($"PIN 设置失败: {error}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("PIN 设置流程异常", ex);
        }
    }

    private async void ChangePin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "更改 PIN",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = FindResource("BgPrimaryBrush") as System.Windows.Media.Brush,
            ResizeMode = ResizeMode.NoResize,
        };

        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(24) };
        for (int i = 0; i < 6; i++)
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

        var title = new System.Windows.Controls.TextBlock
        {
            Text = "设置访问 PIN",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = FindResource("TextPrimaryBrush") as System.Windows.Media.Brush,
            Margin = new Thickness(0, 0, 0, 16),
        };
        System.Windows.Controls.Grid.SetRow(title, 0);
        grid.Children.Add(title);

        var newLabel = new System.Windows.Controls.TextBlock
        {
            Text = "新 PIN（4-16 位）",
            FontSize = 10,
            Foreground = FindResource("TextSecondaryBrush") as System.Windows.Media.Brush,
            Margin = new Thickness(0, 0, 0, 4),
        };
        System.Windows.Controls.Grid.SetRow(newLabel, 1);
        grid.Children.Add(newLabel);

        var newbox = new System.Windows.Controls.PasswordBox
        {
            Style = FindResource("DarkPasswordBox") as Style,
            Margin = new Thickness(0, 0, 0, 10),
        };
        System.Windows.Controls.Grid.SetRow(newbox, 2);
        grid.Children.Add(newbox);

        var confirmLabel = new System.Windows.Controls.TextBlock
        {
            Text = "确认新 PIN",
            FontSize = 10,
            Foreground = FindResource("TextSecondaryBrush") as System.Windows.Media.Brush,
            Margin = new Thickness(0, 0, 0, 4),
        };
        System.Windows.Controls.Grid.SetRow(confirmLabel, 3);
        grid.Children.Add(confirmLabel);

        var confirmbox = new System.Windows.Controls.PasswordBox
        {
            Style = FindResource("DarkPasswordBox") as Style,
            Margin = new Thickness(0, 0, 0, 10),
        };
        System.Windows.Controls.Grid.SetRow(confirmbox, 4);
        grid.Children.Add(confirmbox);

        var msg = new System.Windows.Controls.TextBlock
        {
            FontSize = 10,
            Foreground = FindResource("DangerBrush") as System.Windows.Media.Brush,
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        };
        System.Windows.Controls.Grid.SetRow(msg, 5);
        grid.Children.Add(msg);

        var buttonRow = new System.Windows.Controls.Grid
        {
            ColumnDefinitions =
            {
                new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto },
                new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto },
            }
        };
        System.Windows.Controls.Grid.SetRow(buttonRow, 6);
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(buttonRow);

        var hint = new System.Windows.Controls.TextBlock
        {
            Text = "本机客户端免登录",
            FontSize = 9,
            Foreground = FindResource("TextMutedBrush") as System.Windows.Media.Brush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Controls.Grid.SetColumn(hint, 0);
        buttonRow.Children.Add(hint);

        var cancel = new System.Windows.Controls.Button
        {
            Content = "取消",
            Style = FindResource("SecondaryButton") as Style,
            Padding = new Thickness(20, 8, 20, 8),
            Margin = new Thickness(0, 0, 8, 0),
        };
        System.Windows.Controls.Grid.SetColumn(cancel, 1);
        buttonRow.Children.Add(cancel);

        var submit = new System.Windows.Controls.Button
        {
            Content = "确认修改",
            Style = FindResource("AccentButton") as Style,
            Padding = new Thickness(20, 8, 20, 8),
            IsDefault = true,
        };
        System.Windows.Controls.Grid.SetColumn(submit, 2);
        buttonRow.Children.Add(submit);

        cancel.Click += (_, _) => dialog.Close();

        submit.Click += async (_, _) =>
        {
            var newPin = newbox.Password;
            var confirm = confirmbox.Password;
            if (string.IsNullOrEmpty(newPin))
            {
                msg.Text = "请输入新 PIN";
                return;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(newPin,
                    @"^[A-Za-z0-9!@#$%^&*()_\-+=\[\]{}:;,.?/|~]{4,16}$"))
            {
                msg.Text = "PIN 格式无效（4-16 位字母/数字/安全字符）";
                return;
            }
            if (newPin != confirm)
            {
                msg.Text = "两次输入的 PIN 不一致";
                return;
            }
            // 按设计：改 PIN 也走 reset_pin 接口（仅限本机），不要求旧 PIN
            var result = await _vm.ResetPinAsync(newPin);
            if (result?.Success == true)
            {
                msg.Foreground = FindResource("SuccessBrush") as System.Windows.Media.Brush;
                msg.Text = "PIN 修改成功";
                await Task.Delay(800);
                dialog.Close();
            }
            else
            {
                msg.Text = result?.Error ?? "修改失败";
            }
        };

        dialog.Content = grid;
        dialog.Loaded += (_, _) => newbox.Focus();
        dialog.ShowDialog();
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        _forceExit = true;
        _vm.StopPolling();
        _server?.Stop();
        _notifyIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void VolumeIcon_Click(object sender, RoutedEventArgs e) => _vm.ToggleMuteCommand.Execute(null);

    // ============================================
    //  横屏 / 竖屏 切换与尺寸适配
    // ============================================

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateOrientation();
        if (_vm.IsServerRunning)
            UpdateElementSizes();
    }

    private void UpdateOrientation()
    {
        if (!_vm.IsServerRunning) return;
        var w = ActualWidth;
        var h = ActualHeight - 48; // 减去标题栏高度
        bool landscape = w > h;
        if (landscape == _isLandscape && (landscape ? LandscapeGrid.Visibility : PortraitGrid.Visibility) == Visibility.Visible)
            return;
        _isLandscape = landscape;
        SwitchToCurrentOrientation();
    }

    private void SwitchToCurrentOrientation()
    {
        if (_isLandscape)
        {
            PortraitGrid.Visibility = Visibility.Collapsed;
            LandscapeGrid.Visibility = Visibility.Visible;
        }
        else
        {
            LandscapeGrid.Visibility = Visibility.Collapsed;
            PortraitGrid.Visibility = Visibility.Visible;
        }
    }

    private void UpdateElementSizes()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // —— 竖屏尺寸 ——
            var pw = PortraitGrid.ActualWidth;
            if (pw <= 0) pw = Math.Min(ActualWidth, 520);
            if (pw > 0)
            {
                var pAlbum = Math.Clamp((int)(pw * 0.28), 80, 140);
                P_AlbumBorder.Width = pAlbum + 8;
                P_AlbumBorder.Height = pAlbum + 8;
                P_AlbumImage.Width = pAlbum;
                P_AlbumImage.Height = pAlbum;
                P_AlbumImage.Clip = new System.Windows.Media.RectangleGeometry(
                    new System.Windows.Rect(0, 0, pAlbum, pAlbum), 8, 8);

                var pQr = Math.Clamp((int)(pw * 0.24), 70, 120);
                P_QrBorder.Width = pQr + 8;
                P_QrBorder.Height = pQr + 8;
                P_QrImage.Width = pQr;
                P_QrImage.Height = pQr;
            }

            // —— 横屏尺寸 ——
            var lw = LandscapeGrid.ActualWidth;
            if (lw <= 0) lw = Math.Max(ActualWidth, 600);
            if (lw > 0)
            {
                // 横屏总宽按 1:1.2:1 分配三栏，左栏约占 32%
                double leftCol = lw * 0.32;
                // 封面占左栏内容宽度的 85%，减去 Margin/Padding
                var lAlbum = Math.Clamp((int)(leftCol * 0.70), 140, 260);
                L_AlbumBorder.Width = lAlbum + 12;
                L_AlbumBorder.Height = lAlbum + 12;
                L_AlbumImage.Width = lAlbum;
                L_AlbumImage.Height = lAlbum;
                L_AlbumImage.Clip = new System.Windows.Media.RectangleGeometry(
                    new System.Windows.Rect(0, 0, lAlbum, lAlbum), 12, 12);

                // 二维码：右栏宽度的 70%
                double rightCol = lw * 0.35;
                var lQr = Math.Clamp((int)(rightCol * 0.60), 110, 180);
                L_QrBorder.Width = lQr + 10;
                L_QrBorder.Height = lQr + 10;
                L_QrImage.Width = lQr;
                L_QrImage.Height = lQr;
            }
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    private static string FindPython()
    {
        var paths = new[]
        {
            @$"C:\Users\{Environment.UserName}\AppData\Local\Programs\Python\Python314\python.exe",
            @"C:\Python314\python.exe",
            @"C:\Python312\python.exe",
            @"C:\Python311\python.exe",
        };
        foreach (var p in paths)
            if (File.Exists(p)) return p;
        return "python";
    }

    private static string FindServerDir()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "server"),
            Path.Combine(baseDir, "..", "..", "..", "server"),
            Path.Combine(baseDir, "..", "..", "server"),
            Path.Combine(baseDir, "..", "server"),
            Path.Combine(baseDir, "server"),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (Directory.Exists(full)) return full;
        }
        return Path.Combine(baseDir, "server");
    }
}
