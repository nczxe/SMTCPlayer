using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SMTCPlayer.Core.Services;
using SMTCPlayer.Logging;
using Windows.Storage.Pickers;
using LogLevel = SMTCPlayer.Logging.LogLevel;

namespace SMTCPlayer.WinUI;

/// <summary>
/// 独立日志查看器：从内存环形缓冲回填并实时订阅 <see cref="LogManager.EntryLogged"/> 滚动展示。
/// 支持暂停/继续（暂停期间只累积不显示，恢复后补齐）、级别/分类过滤、关键字搜索、
/// 自动滚动与导出当前视图。单例持有与生命周期由 MainWindow 负责。
/// </summary>
public sealed partial class LogViewerWindow : Window
{
    private const string CategoryAll = "全部";

    /// <summary>内存视图上限：超过后按批裁剪最旧条目，避免长期驻留增长。</summary>
    private const int MaxEntries = 4000;
    private const int TrimBatch = 1000;

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _renderTimer;
    private readonly List<LogEntry> _all = new();
    private readonly FontFamily _monoFont = new("Consolas");

    private bool _paused;
    private bool _dirty;
    private bool _loadingFilters = true;
    private int _minLevel = (int)LogLevel.Debug;
    private string _categoryFilter = CategoryAll;

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    /// <param name="theme">跟随主窗口的当前主题。</param>
    public LogViewerWindow(ElementTheme theme)
    {
        InitializeComponent();

        _dispatcher = DispatcherQueue.GetForCurrentThread();

        Title = "日志查看器";
        RootGrid.RequestedTheme = theme;
        TryEnableMicaBackdrop();
        ResizeToLogical(920, 620);

        LevelFilter.Items.Add("调试");
        LevelFilter.Items.Add("信息");
        LevelFilter.Items.Add("警告");
        LevelFilter.Items.Add("错误");
        LevelFilter.SelectedIndex = 0;
        _loadingFilters = false;

        // 渲染节流：日志可能高频到达，合并到定时器统一重绘
        _renderTimer = _dispatcher.CreateTimer();
        _renderTimer.Interval = TimeSpan.FromMilliseconds(120);
        _renderTimer.Tick += (_, _) =>
        {
            if (_dirty && !_paused)
            {
                _dirty = false;
                RebuildView();
            }
        };
        _renderTimer.Start();

        LogManager.EntryLogged += OnEntryLogged;
        Closed += (_, _) =>
        {
            try
            {
                LogManager.EntryLogged -= OnEntryLogged;
                _renderTimer.Stop();
            }
            catch { /* 解绑失败不影响关闭 */ }
        };

        Backfill();
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

    private static Brush GetBrush(string key, string fallbackKey)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            return brush;
        return (Brush)Application.Current.Resources[fallbackKey];
    }

    // ============== 初始回填与实时订阅 ==============

    /// <summary>打开时从环形缓冲回填最近条目。</summary>
    private void Backfill()
    {
        foreach (var entry in LogManager.SnapshotEntries())
            _all.Add(entry);

        RefreshCategoryFilter();
        RebuildView();
        UpdateStatus();
    }

    /// <summary>发布线程回调：切回 UI 线程累积；暂停期间只累积不渲染，恢复时统一补齐。</summary>
    private void OnEntryLogged(LogEntry entry)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _all.Add(entry);

            // 出现新分类时刷新分类下拉项（保留当前选择）
            if (CategoryFilter.Items.IndexOf(entry.Category) < 0)
                RefreshCategoryFilter();

            if (_paused) return;

            if (_all.Count > MaxEntries)
                _dirty = true; // 交由定时器全量重建（同时裁剪最旧）
            else if (PassesFilter(entry))
                AppendViewItem(entry);

            UpdateStatus();
        });
    }

    // ============== 渲染 ==============

    /// <summary>按当前过滤条件全量重建视图（过滤变化 / 恢复 / 裁剪后调用）。</summary>
    private void RebuildView()
    {
        if (_all.Count > MaxEntries)
            _all.RemoveRange(0, _all.Count - MaxEntries);

        LogList.Items.Clear();
        foreach (var entry in _all)
        {
            if (PassesFilter(entry))
                LogList.Items.Add(BuildBlock(entry));
        }

        if (AutoScrollToggle.IsOn)
            ScrollToEnd();
        UpdateStatus();
    }

    private void AppendViewItem(LogEntry entry)
    {
        var block = BuildBlock(entry);
        LogList.Items.Add(block);
        if (AutoScrollToggle.IsOn)
            LogList.ScrollIntoView(block, ScrollIntoViewAlignment.Default);
    }

    private TextBlock BuildBlock(LogEntry entry) => new()
    {
        Text = $"[{entry.Timestamp:HH:mm:ss.fff}] [{entry.Level}] [{entry.Category}] {entry.Message}",
        FontFamily = _monoFont,
        FontSize = 12,
        TextWrapping = TextWrapping.NoWrap,
        Foreground = BrushForLevel(entry.Level),
    };

    private static Brush BrushForLevel(LogLevel level) => level switch
    {
        LogLevel.Error => GetBrush("SystemFillColorCriticalBrush", "TextFillColorPrimaryBrush"),
        LogLevel.Warn => GetBrush("SystemFillColorCautionBrush", "TextFillColorPrimaryBrush"),
        LogLevel.Info => GetBrush("TextFillColorPrimaryBrush", "TextFillColorPrimaryBrush"),
        _ => GetBrush("TextFillColorSecondaryBrush", "TextFillColorPrimaryBrush"),
    };

    private void ScrollToEnd()
    {
        if (LogList.Items.Count == 0) return;
        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1], ScrollIntoViewAlignment.Default);
    }

    private bool PassesFilter(LogEntry entry)
    {
        if ((int)entry.Level < _minLevel) return false;
        if (_categoryFilter != CategoryAll && entry.Category != _categoryFilter) return false;

        var keyword = SearchBox.Text?.Trim();
        if (!string.IsNullOrEmpty(keyword)
            && entry.Message.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0
            && entry.Category.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        return true;
    }

    // ============== 过滤与搜索 ==============

    private void RefreshCategoryFilter()
    {
        var previous = _categoryFilter;
        _loadingFilters = true;

        CategoryFilter.Items.Clear();
        CategoryFilter.Items.Add(CategoryAll);
        foreach (var category in _all.Select(x => x.Category).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            CategoryFilter.Items.Add(category);

        var index = CategoryFilter.Items.IndexOf(previous);
        CategoryFilter.SelectedIndex = index >= 0 ? index : 0;

        _loadingFilters = false;
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFilters) return;

        _minLevel = LevelFilter.SelectedIndex switch
        {
            1 => (int)LogLevel.Info,
            2 => (int)LogLevel.Warn,
            3 => (int)LogLevel.Error,
            _ => (int)LogLevel.Debug,
        };
        _categoryFilter = CategoryFilter.SelectedItem as string ?? CategoryAll;
        RebuildView();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingFilters) return;
        RebuildView();
    }

    // ============== 暂停 / 导出 / 打开目录 ==============

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        if (_paused)
        {
            PauseText.Text = "继续";
            PauseIcon.Glyph = "\uE768";
        }
        else
        {
            PauseText.Text = "暂停";
            PauseIcon.Glyph = "\uE769";
            _dirty = true; // 恢复后补齐暂停期间缓冲的条目
        }
        UpdateStatus();
        Logger.Info(_paused ? "日志查看器已暂停" : "日志查看器已继续");
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var lines = LogList.Items.OfType<TextBlock>().Select(t => t.Text).ToArray();
            if (lines.Length == 0)
            {
                SetStatus("当前视图为空，无可导出内容", isError: true);
                return;
            }

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"smtc-logs-{DateTime.Now:yyyyMMdd-HHmmss}",
            };
            picker.FileTypeChoices.Add("文本文件", new List<string> { ".txt" });
            picker.FileTypeChoices.Add("日志文件", new List<string> { ".log" });
            // WinUI 3 桌面应用：Picker 需与窗口句柄关联，否则无法弹出
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var file = await picker.PickSaveFileAsync();
            if (file == null) return; // 用户取消

            await File.WriteAllLinesAsync(file.Path, lines);
            SetStatus($"已导出 {lines.Length} 行到 {file.Path}");
            Logger.Info($"日志查看器：已导出 {lines.Length} 行到 {file.Path}");
        }
        catch (Exception ex)
        {
            SetStatus($"导出失败：{ex.Message}", isError: true);
            Logger.Warn($"日志查看器导出失败: {ex.Message}");
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var logDir = Path.GetDirectoryName(LogManager.LogFilePath) ?? "";
            if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir))
            {
                SetStatus("日志目录不存在", isError: true);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", logDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"打开日志文件夹失败：{ex.Message}", isError: true);
            Logger.Warn($"日志查看器打开日志文件夹失败: {ex.Message}");
        }
    }

    // ============== 状态反馈 ==============

    private void UpdateStatus()
    {
        if (StatusText is null) return;
        var shown = LogList.Items.Count;
        StatusText.Text = _paused
            ? $"已暂停 · 累计 {_all.Count} 条 · 显示 {shown} 条（恢复后补齐）"
            : $"累计 {_all.Count} 条 · 显示 {shown} 条";
        StatusText.Foreground = GetBrush("TextFillColorSecondaryBrush", "TextFillColorPrimaryBrush");
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? GetBrush("SystemFillColorCriticalBrush", "TextFillColorPrimaryBrush")
            : GetBrush("TextFillColorSecondaryBrush", "TextFillColorPrimaryBrush");
    }
}
