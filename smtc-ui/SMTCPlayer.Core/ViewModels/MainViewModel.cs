using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SMTCPlayer.Core.Models;
using SMTCPlayer.Core.Services;

namespace SMTCPlayer.Core.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly SmtcApiClient _api;
    private readonly FlaskServerManager? _server;
    private readonly IClipboardService? _clipboard;
    private readonly SynchronizationContext? _syncContext;
    private System.Timers.Timer? _pollTimer;
    private int _port;

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
    private bool _ncmLoggedIn;
    private string _ncmNickname = "";
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
        ClearCookiesCommand = new RelayCommand(async _ =>
        {
            try { await ClearNcmCookiesAsync(); }
            catch (Exception ex) { Logger.Warn($"清除 Cookie 失败: {ex.Message}"); }
        }, _ => IsServerRunning);

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
            ((RelayCommand)ClearCookiesCommand).RaiseCanExecuteChanged();
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

    public double VolumeValue
    {
        get => _volumeValue;
        set
        {
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
    public bool IsPlaying => PlayerStatus?.IsPlaying ?? false;
    public bool HasPrevious => PlayerStatus?.HasPrevious ?? false;
    public bool HasNext => PlayerStatus?.HasNext ?? false;
    public double ProgressPercent => PlayerStatus?.ProgressPercent ?? 0;
    public string PositionText => PlayerStatus?.PositionText ?? "0:00";
    public string DurationText => PlayerStatus?.DurationText ?? "0:00";

    public bool IsAuthenticated => _api.HasToken;
    public string HealthText =>
        $"SMTC {(_smtcAvailable ? "✓" : "✗")} · Watcher {(_watcherAvailable ? "✓" : "✗")} · 音量 {(_volumeAvailable ? "✓" : "✗")} · NCM {(_ncmLoggedIn ? _ncmNickname : "未登录")}";

    public ICommand StartServerCommand { get; }
    public ICommand StopServerCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand CopyUrlCommand { get; }
    public ICommand ClearCookiesCommand { get; }

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
            _ = PollStatusAsync();
            _ = PollHealthAsync();
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

    public async Task<bool> ClearNcmCookiesAsync()
    {
        Logger.Info("清除网易云 Cookie");
        var ok = await _api.ClearNcmCookiesAsync();
        if (ok)
        {
            _ncmLoggedIn = false;
            _ncmNickname = "";
            OnPropertyChanged(nameof(HealthText));
            Logger.Info("网易云 Cookie 已清除");
        }
        else
        {
            Logger.Warn("清除网易云 Cookie 失败");
        }
        return ok;
    }

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

                PlayerStatus = status;

                // 如果处于音量保护期，保留本地音量/静音状态
                if (!IsVolumeProtected())
                {
                    _volumeValue = status.Volume;
                    _isMuted = status.Muted;
                    OnPropertyChanged(nameof(VolumeValue));
                    OnPropertyChanged(nameof(IsMuted));
                    OnPropertyChanged(nameof(VolumeText));
                }

                // 统一触发所有派生属性通知
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Artist));
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
                _ncmLoggedIn = health.NcmApi?.LoggedIn ?? false;
                _ncmNickname = health.NcmApi?.Nickname ?? "";
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
