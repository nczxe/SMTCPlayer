using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SMTCPlayer.Core.ViewModels;

namespace SMTCPlayer.WinUI.Dialogs;

/// <summary>
/// 歌曲进度偏移校准对话框。
/// 上半区以 500ms 间隔从 ViewModel 读取（已应用偏移的）播放进度实时渲染；
/// 偏移调整时基于最近一次读取的原始进度立即重算，无需等待下一次轮询。
/// </summary>
public sealed partial class PositionOffsetDialog : ContentDialog
{
    private const int MaxOffsetMs = 5000;

    private readonly MainViewModel _vm;
    private readonly Microsoft.UI.Xaml.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>最近一次渲染的已校正进度（秒）</summary>
    private double _lastShownPosition;

    /// <summary>最近一次渲染时使用的偏移（毫秒），用于从已校正值还原原始进度</summary>
    private int _offsetAtLastRender;

    private double _lastDuration;
    private bool _updatingInput;

    /// <summary>对话框当前的偏移值（毫秒）。保存时由宿主读取。</summary>
    public int OffsetMs { get; private set; }

    /// <summary>偏移变化回调（实时预览用）：宿主应立即写入 ViewModel 使主界面同步生效。</summary>
    public Action<int>? OffsetChanged { get; set; }

    public PositionOffsetDialog(MainViewModel vm, int initialOffsetMs)
    {
        InitializeComponent();
        _vm = vm;

        OffsetMs = Math.Clamp(initialOffsetMs, -MaxOffsetMs, MaxOffsetMs);
        _updatingInput = true;
        OffsetInput.Value = OffsetMs;
        _updatingInput = false;

        RenderFromVm();
        _timer.Tick += (_, _) => RenderFromVm();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    // ============== 偏移调整 ==============

    private void Plus10Button_Click(object sender, RoutedEventArgs e) => SetOffset(OffsetMs + 10);

    private void Plus1Button_Click(object sender, RoutedEventArgs e) => SetOffset(OffsetMs + 1);

    private void Minus1Button_Click(object sender, RoutedEventArgs e) => SetOffset(OffsetMs - 1);

    private void Minus10Button_Click(object sender, RoutedEventArgs e) => SetOffset(OffsetMs - 10);

    private void ResetButton_Click(object sender, RoutedEventArgs e) => SetOffset(0);

    private void OffsetInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingInput) return;
        if (!double.IsFinite(args.NewValue)) return;
        SetOffset((int)Math.Round(args.NewValue));
    }

    private void SetOffset(int ms)
    {
        var clamped = Math.Clamp(ms, -MaxOffsetMs, MaxOffsetMs);
        var changed = clamped != OffsetMs;
        OffsetMs = clamped;

        // 同步输入框显示（含越界回弹到边界值）
        _updatingInput = true;
        OffsetInput.Value = clamped;
        _updatingInput = false;

        if (!changed) return;

        // 基于最近一次读取的原始进度立即重算显示，不等下一次轮询
        var raw = _lastShownPosition - _offsetAtLastRender / 1000.0;
        _lastShownPosition = Math.Max(0, raw + OffsetMs / 1000.0);
        _offsetAtLastRender = OffsetMs;
        UpdateProgressUi();

        OffsetChanged?.Invoke(OffsetMs);
    }

    // ============== 实时进度渲染 ==============

    private void RenderFromVm()
    {
        var ps = _vm.PlayerStatus;
        if (ps == null) return;

        _lastShownPosition = ps.Position;
        _lastDuration = ps.Duration;
        _offsetAtLastRender = OffsetMs;
        UpdateProgressUi();
    }

    private void UpdateProgressUi()
    {
        var dur = _lastDuration;
        PosBar.Maximum = dur > 0 ? dur : 1;
        PosBar.Value = dur > 0 ? Math.Clamp(_lastShownPosition, 0, dur) : 0;
        PosText.Text = FormatTime(_lastShownPosition);
        DurText.Text = FormatTime(dur);

        var title = _vm.Title;
        TrackText.Text = string.IsNullOrWhiteSpace(title)
            ? "未在播放"
            : $"{title} — {_vm.Artist}";
    }

    /// <summary>校准场景需要亚秒精度：格式化为 分:秒.十分位。</summary>
    private static string FormatTime(double seconds)
    {
        if (seconds <= 0) return "0:00.0";
        int m = (int)(seconds / 60);
        var s = seconds - m * 60;
        return $"{m}:{s:00.0}";
    }
}
