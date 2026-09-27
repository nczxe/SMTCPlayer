using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SMTCPlayer.Core.Models;
using SMTCPlayer.Core.Plugins;
using SMTCPlayer.Core.Services;
using SMTCPlayer.PluginApi;
using SMTCPlayer.PluginSystem;

namespace SMTCPlayer.Core.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly SmtcApiClient _api;
    private readonly FlaskServerManager? _server;
    private readonly IClipboardService? _clipboard;
    private readonly SynchronizationContext? _syncContext;
    private System.Timers.Timer? _pollTimer;
    private System.Timers.Timer? _pluginJobTimer;
    private int _port;
    private MediaSnapshot? _lastSnapshot;

    private bool _isServerRunning;
    private string _statusText = "";
    private string _lanUrl = "";
    private PlayerStatus _playerStatus = new();
    private double _volumeValue = 0;
    private bool _isMuted;
    private string? _thumbnailUrl;
    private int _polling;
    private int _pollFailures;
    private int _healthTick;
    private bool _smtcAvailable;
    private bool _watcherAvailable;
    private bool _volumeAvailable;
    private long _lastVolumeManipulatedAt;
    private long _lastPlaybackManipulatedAt;

    public MainViewModel(SmtcApiClient api, FlaskServerManager? server, int port, IClipboardService? clipboard)
    {
        Logger.Info("MainViewModel 初始化");
        _api = api;
        _server = server;
        _clipboard = clipboard;
        _syncContext = SynchronizationContext.Current; // 捕获 UI 线程同步上下文
        _playerStatus = new PlayerStatus();
        _port = port > 0 ? port : 8888;

        // 插件宿主：SMTC 变化 → 轮询 diff → 广播事件 → 所有插件
        PluginManager = new PluginManager(api.BaseUrl);
        _ = PluginManager.InitializeAsync();

        // 启动静默检查更新（24 小时节流；只检查提示，不自动下载安装）
        _ = RaiseStartupUpdateIfAnyAsync();

        StartServerCommand = new RelayCommand(async _ =>
        {
            try { await StartServerAsync(); }
            catch (Exception ex)
            {
                Logger.Error("StartServerCommand 异常", ex);
                StatusText = $"启动失败: {ex.Message}";
            }
        });
        StopServerCommand = new RelayCommand(_ =>
        {
            try { StopServer(); }
            catch (Exception ex) { Logger.Warn($"StopServer 失败: {ex.Message}"); }
        }, _ => IsServerRunning);
        PlayPauseCommand = new RelayCommand(async _ =>
        {
            try
            {
                // 乐观更新：立刻切换播放状态，让 UI 不用等轮询
                _playerStatus.IsPlaying = !_playerStatus.IsPlaying;
                OnPropertyChanged(nameof(IsPlaying));
                MarkPlaybackManipulated();
                await _api.SendControlAsync("play_pause");
            }
            catch (Exception ex)
            {
                // 失败时回滚
                _playerStatus.IsPlaying = !_playerStatus.IsPlaying;
                OnPropertyChanged(nameof(IsPlaying));
                Logger.Warn($"PlayPause 失败: {ex.Message}");
            }
        }, _ => IsServerRunning);
        NextCommand = new RelayCommand(async _ =>
        {
            try
            {
                MarkPlaybackManipulated();
                await _api.SendControlAsync("next");
            }
            catch (Exception ex) { Logger.Warn($"Next 失败: {ex.Message}"); }
        }, _ => IsServerRunning);
        PreviousCommand = new RelayCommand(async _ =>
        {
            try
            {
                MarkPlaybackManipulated();
                await _api.SendControlAsync("previous");
            }
            catch (Exception ex) { Logger.Warn($"Previous 失败: {ex.Message}"); }
        }, _ => IsServerRunning);
        ToggleMuteCommand = new RelayCommand(async _ =>
        {
            try
            {
                IsMuted = !IsMuted;
                MarkVolumeManipulated();
                await _api.ToggleMuteAsync();
            }
            catch (Exception ex)
            {
                IsMuted = !IsMuted;
                Logger.Warn($"ToggleMute 失败: {ex.Message}");
            }
        }, _ => IsServerRunning);
        CopyUrlCommand = new RelayCommand(_ =>
        {
            try
            {
                _clipboard?.SetText(LanUrl);
                Logger.Info($"已复制 URL: {LanUrl}");
            }
            catch (Exception ex) { Logger.Warn($"复制 URL 失败: {ex.Message}"); }
        }, _ => !string.IsNullOrEmpty(LanUrl));

        StartButtonText = "启动服务";
    }

    private void StopServer()
    {
        Logger.Info("=== 停止服务 ===");
        StatusText = "正在停止服务...";
        StopPolling();
        _server?.Stop();
        IsServerRunning = false;
        LanUrl = "";
        StatusText = "服务已停止";
        PluginManager.PublishServerState(false);
    }

    public int Port
    {
        get => _port;
        set { _port = value; OnPropertyChanged(); }
    }

    public bool IsServerRunning
    {
        get => _isServerRunning;
        set
        {
            _isServerRunning = value;
            OnPropertyChanged();
            StartButtonText = value ? "重启服务" : "启动服务";
            ((RelayCommand)StopServerCommand).RaiseCanExecuteChanged();
            ((RelayCommand)PlayPauseCommand).RaiseCanExecuteChanged();
            ((RelayCommand)NextCommand).RaiseCanExecuteChanged();
            ((RelayCommand)PreviousCommand).RaiseCanExecuteChanged();
            ((RelayCommand)ToggleMuteCommand).RaiseCanExecuteChanged();
        }
    }

    private string _startButtonText = "启动服务";
    public string StartButtonText
    {
        get => _startButtonText;
        set { _startButtonText = value; OnPropertyChanged(); }
    }

    public string Host => "0.0.0.0";

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public string LanUrl
    {
        get => _lanUrl;
        set
        {
            _lanUrl = value;
            OnPropertyChanged();
            ((RelayCommand)CopyUrlCommand).RaiseCanExecuteChanged();
        }
    }

    public PlayerStatus PlayerStatus
    {
        get => _playerStatus;
        set { _playerStatus = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 歌曲进度偏移（秒）：当网易云增强监视器读取的进度与实际播放位置存在微小偏差时，
    /// 由用户手动校准。仅对 watcher 提供的进度生效（NeteaseWatcherActive），
    /// 不影响其他 SMTC 音源。UI 层负责持久化并在启动/调整时写入此属性。
    /// </summary>
    public double PositionOffsetSeconds { get; set; }

    /// <summary>插件宿主（供 UI 层读取插件列表 / 切换启用状态）。</summary>
    public PluginManager PluginManager { get; }

    /// <summary>启动静默检查发现新版本时触发（已 marshal 到创建 ViewModel 的线程）。</summary>
    public event Action<UpdateInfo>? UpdateAvailable;

    /// <summary>启动静默检查更新：24 小时节流，仅提示不自动下载。</summary>
    private async Task RaiseStartupUpdateIfAnyAsync()
    {
        try
        {
            var checker = new UpdateChecker();
            var info = await checker.TryGetPendingStartupUpdateAsync();
            if (info == null) return;

            Logger.Info($"启动检查发现新版本 v{info.LatestVersion}（当前 v{info.CurrentVersion}），等待 UI 提醒");
            var handler = UpdateAvailable;
            if (handler == null) return;
            if (_syncContext != null) _syncContext.Post(_ => handler(info), null);
            else handler(info);
        }
        catch (Exception ex)
        {
            Logger.Warn($"启动检查更新异常: {ex.Message}");
        }
    }

    public double VolumeValue
    {
        get => _volumeValue;
        set
        {
            // 值未变化时不通知：轮询每秒都会带回音量快照，避免 UI 层重复响应刷日志
            if (Math.Abs(_volumeValue - value) < 0.01) return;
            _volumeValue = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
            MarkVolumeManipulated();
            OnVolumeChanged(value);
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;
            _isMuted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
        }
    }

    public string? ThumbnailUrl
    {
        get => _thumbnailUrl;
        set { _thumbnailUrl = value; OnPropertyChanged(); }
    }

    public string VolumeText => $"{Math.Clamp((int)Math.Round(VolumeValue), 0, 100)}%";

    // WPF 兼容的派生属性（直通 PlayerStatus）
    public string Title => PlayerStatus?.Title ?? "";
    public string Artist => PlayerStatus?.Artist ?? "";
    public string AlbumTitle => PlayerStatus?.AlbumTitle ?? "";
    public bool IsPlaying => PlayerStatus?.IsPlaying ?? false;
    public bool HasPrevious => PlayerStatus?.HasPrevious ?? false;
    public bool HasNext => PlayerStatus?.HasNext ?? false;
    public double ProgressPercent => PlayerStatus?.ProgressPercent ?? 0;
    public string PositionText => PlayerStatus?.PositionText ?? "0:00";
    public string DurationText => PlayerStatus?.DurationText ?? "0:00";

    public bool IsAuthenticated => _api.HasToken;
    public string HealthText =>
        $"SMTC {(_smtcAvailable ? "✓" : "✗")} · Watcher {(_watcherAvailable ? "✓" : "✗")} · 音量 {(_volumeAvailable ? "✓" : "✗")}";

    public ICommand StartServerCommand { get; }
    public ICommand StopServerCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand CopyUrlCommand { get; }

    private async Task StartServerAsync()
    {
        Logger.Info("=== 启动服务 ===");
        StatusText = "正在启动服务...";
        try
        {
            if (_server == null)
            {
                Logger.Error("_server 为 null");
                StatusText = "服务管理器未初始化";
                return;
            }

            var error = _server.ValidatePaths();
            if (!string.IsNullOrEmpty(error))
            {
                Logger.Error($"路径验证失败: {error}");
                StatusText = error;
                return;
            }

            Logger.Info($"调用 StartAsync(Port={Port})...");
            _api.SetPort(Port);
            await _server.StartAsync(Port);

            IsServerRunning = true;
            var ip = FlaskServerManager.GetLocalIp();
            LanUrl = $"http://{ip}:{Port}";
            StatusText = "服务器运行中";
            Logger.Info($"服务启动成功: {LanUrl}");

            Logger.Info("启动轮询定时器...");
            _pollTimer?.Stop();
            _pollTimer?.Dispose();
            _pollFailures = 0;
            _pollTimer = new System.Timers.Timer(1000);
            _pollTimer.Elapsed += (_, _) =>
            {
                try
                {
                    PollStatusAsync().GetAwaiter().GetResult();
                    _healthTick++;
                    if (_healthTick % 5 == 0)
                    {
                        PollHealthAsync().GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"轮询异常: {ex.Message}");
                }
            };
            _pollTimer.Start();

            // 插件任务桥轮询（网页端搜索/播放请求经 Flask 队列转来，400ms 取件保证响应速度）
            _pluginJobTimer?.Stop();
            _pluginJobTimer?.Dispose();
            _pluginJobTimer = new System.Timers.Timer(400);
            _pluginJobTimer.Elapsed += (_, _) =>
            {
                try { PluginManager.PollServerJobsAsync().GetAwaiter().GetResult(); }
                catch { /* 桥接异常忽略，下轮重试 */ }
            };
            _pluginJobTimer.Start();

            _ = PollStatusAsync();
            _ = PollHealthAsync();
            PluginManager.PublishServerState(true);
        }
        catch (InvalidOperationException ex)
        {
            Logger.Error("启动失败(InvalidOperationException)", ex);
            StatusText = $"启动失败: {ex.Message}";
        }
        catch (Exception ex)
        {
            Logger.Error("启动失败(Exception)", ex);
            StatusText = $"启动失败: {ex.Message}";
        }
    }

    public async Task<AuthStatus?> GetAuthStatusAsync() => await _api.GetAuthStatusAsync();
    public async Task<AuthResponse?> SetupPinAsync(string pin) => await _api.SetupPinAsync(pin);
    public async Task<AuthResponse?> LoginAsync(string pin) => await _api.LoginAsync(pin);
    public async Task<AuthResponse?> ChangePinAsync(string oldPin, string newPin) => await _api.ChangePinAsync(oldPin, newPin);
    public async Task<AuthResponse?> ResetPinAsync(string newPin) => await _api.ResetPinAsync(newPin);

    public void ClearAuth() => _api.ClearToken();

    // ============== 本地操作保护期 ==============

    private void MarkVolumeManipulated()
    {
        _lastVolumeManipulatedAt = Environment.TickCount64;
    }

    private bool IsVolumeProtected()
    {
        const int PROTECT_MS = 2000;
        return unchecked((ulong)(Environment.TickCount64 - _lastVolumeManipulatedAt)) < PROTECT_MS;
    }

    private void MarkPlaybackManipulated()
    {
        _lastPlaybackManipulatedAt = Environment.TickCount64;
    }

    private bool IsPlaybackProtected()
    {
        const int PROTECT_MS = 2000;
        return unchecked((ulong)(Environment.TickCount64 - _lastPlaybackManipulatedAt)) < PROTECT_MS;
    }

    // ============== 轮询 ==============

    private async Task PollStatusAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            // 上报插件聚合能力（插件启停后 1 秒内随轮询生效；"none" 表示显式清空）
            var caps = PluginManager.GetActiveCapabilities();
            _api.CapabilitiesHeader = caps.Count > 0 ? string.Join(",", caps) : "none";

            var status = await _api.GetStatusAsync();
            if (status != null)
            {
                _pollFailures = 0;

                // 如果处于播放保护期，保留本地乐观的 IsPlaying 状态
                if (IsPlaybackProtected())
                {
                    Logger.Debug($"PollStatusAsync: 播放保护期，保留本地 IsPlaying={_playerStatus.IsPlaying}");
                    status.IsPlaying = _playerStatus.IsPlaying;
                }

                // 进度偏移校准：仅修正网易云增强监视器提供的进度（内存扫描时钟可能存在微小漂移）
                if (status.NeteaseWatcherActive && Math.Abs(PositionOffsetSeconds) > 0.0001)
                {
                    var corrected = status.Position + PositionOffsetSeconds;
                    corrected = status.Duration > 0
                        ? Math.Clamp(corrected, 0, status.Duration)
                        : Math.Max(0, corrected);
                    status.Position = corrected;
                }

                PlayerStatus = status;

                // 如果处于音量保护期，保留本地音量/静音状态；值无变化时不发通知（防刷屏）
                if (!IsVolumeProtected())
                {
                    bool volChanged = Math.Abs(_volumeValue - status.Volume) >= 0.01;
                    bool muteChanged = _isMuted != status.Muted;
                    _volumeValue = status.Volume;
                    _isMuted = status.Muted;
                    if (volChanged) OnPropertyChanged(nameof(VolumeValue));
                    if (muteChanged) OnPropertyChanged(nameof(IsMuted));
                    if (volChanged || muteChanged) OnPropertyChanged(nameof(VolumeText));
                }

                // 统一触发所有派生属性通知
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Artist));
                OnPropertyChanged(nameof(AlbumTitle));
                OnPropertyChanged(nameof(IsPlaying));
                OnPropertyChanged(nameof(HasPrevious));
                OnPropertyChanged(nameof(HasNext));
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(PositionText));
                OnPropertyChanged(nameof(DurationText));

                // 封面 URL：仅在 URL 变化时更新（切歌时才重新加载）
                var newThumb = string.IsNullOrWhiteSpace(status.Thumbnail) ? null : status.Thumbnail;
                if (newThumb != _thumbnailUrl)
                {
                    ThumbnailUrl = newThumb;
                }

                // 插件广播：投影为快照 → diff 出变化事件 → 广播给所有插件
                var snapshot = PluginEventDispatcher.ToSnapshot(status);
                PluginManager.UpdateSnapshot(snapshot);
                var events = PluginEventDispatcher.Diff(_lastSnapshot, snapshot);
                if (events.Count > 0)
                {
                    PluginManager.Publish(events);
                }
                _lastSnapshot = snapshot;
            }
            else
            {
                HandlePollFailure();
            }
        }
        catch
        {
            HandlePollFailure();
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void HandlePollFailure()
    {
        _pollFailures++;
        if (_pollFailures >= 5)
        {
            Logger.Warn("轮询连续失败，判定服务器已断开");
            StatusText = "服务器已断开";
            IsServerRunning = false;
            StopPolling();
            PluginManager.PublishServerState(false);
        }
    }

    private async Task PollHealthAsync()
    {
        try
        {
            var health = await _api.GetHealthAsync();
            if (health != null)
            {
                _smtcAvailable = health.Smtc?.Available ?? false;
                _watcherAvailable = health.NeteaseWatcher?.Available ?? false;
                _volumeAvailable = health.Volume?.Available ?? false;
                OnPropertyChanged(nameof(HealthText));
            }
        }
        catch
        {
        }
    }

    private async void OnVolumeChanged(double value)
    {
        try
        {
            Logger.Debug($"OnVolumeChanged: 将音量 {value} 发往服务端");
            await _api.SetVolumeAsync(value);
        }
        catch { }
    }

    public void StopPolling()
    {
        Logger.Info("停止轮询");
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        _pluginJobTimer?.Stop();
        _pluginJobTimer?.Dispose();
    }

    /// <summary>通知所有插件卸载并保存状态（应用退出时调用）。</summary>
    public void ShutdownPlugins()
    {
        try
        {
            PluginManager.ShutdownAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.Warn($"插件关闭异常: {ex.Message}");
        }
    }

    /// <summary>ShutdownPlugins 的异步版本：供 UI 线程调用，避免同步等待造成卡死。</summary>
    public async Task ShutdownPluginsAsync()
    {
        try
        {
            await PluginManager.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"插件关闭异常: {ex.Message}");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler == null) return;

        // 如果有 UI 线程同步上下文，将 PropertyChanged 投递到 UI 线程
        // 修复 WinUI x:Bind 在后台线程触发时不更新 UI 的问题
        if (_syncContext != null)
            _syncContext.Post(_ => handler(this, new PropertyChangedEventArgs(name!)), null);
        else
            handler(this, new PropertyChangedEventArgs(name!));
    }
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
