using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SMTCPlayer.Core.Services;

namespace SMTCPlayer.Wpf;

public partial class DebugWindow : Window
{
    private static DebugWindow? _instance;
    private static readonly ConcurrentQueue<string> _logQueue = new();
    private readonly DispatcherTimer _refreshTimer;
    private FileSystemWatcher? _logWatcher;
    private string _logFilePath = "";

    public static bool IsOpen => _instance != null;

    public static DebugWindow Open(Window owner)
    {
        if (_instance is { IsLoaded: true })
        {
            _instance.Activate();
            return _instance;
        }

        var window = new DebugWindow { Owner = owner };
        window.Show();
        return window;
    }

    public static void CloseAll()
    {
        if (_instance != null)
        {
            try { _instance.Close(); } catch { }
        }
    }

    public DebugWindow()
    {
        InitializeComponent();
        _instance = this;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += (_, _) => FlushLogs();
        _refreshTimer.Start();

        Loaded += (_, _) =>
        {
            Logger.DebugWindowSink = msg => WriteLog(msg);
            AppendLog($"[调试窗口] 已打开");
            AppendLog($"[系统] OS: {Environment.OSVersion}");
            AppendLog($"[系统] .NET: {Environment.Version}");
            AppendLog($"[系统] 工作目录: {AppDomain.CurrentDomain.BaseDirectory}");
            AppendLog($"[系统] 日志文件: {Logger.LogFilePath}");
            AppendLog("");
            StartLogWatcher();
            LoadExistingLogs();
        };
    }

    public static void WriteLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        _logQueue.Enqueue(line);
        _instance?.Dispatcher.BeginInvoke(() => _instance?.FlushLogs());
    }

    private void FlushLogs()
    {
        while (_logQueue.TryDequeue(out var line))
        {
            LogTextBox.AppendText(line + Environment.NewLine);
        }
        if (AutoScrollCheckBox.IsChecked == true)
        {
            LogTextBox.ScrollToEnd();
        }
        StatusText.Text = $"行数: {LogTextBox.LineCount}";
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText(message + Environment.NewLine);
        if (AutoScrollCheckBox.IsChecked == true)
            LogTextBox.ScrollToEnd();
    }

    private void StartLogWatcher()
    {
        _logFilePath = Logger.LogFilePath;
        if (string.IsNullOrEmpty(_logFilePath) || !File.Exists(_logFilePath)) return;

        var logDir = Path.GetDirectoryName(_logFilePath)!;
        _logWatcher = new FileSystemWatcher(logDir, "*.log")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _logWatcher.Changed += (_, _) => Dispatcher.BeginInvoke(LoadNewLogContent);
        _logWatcher.Created += (_, _) => Dispatcher.BeginInvoke(LoadNewLogContent);
    }

    private long _lastReadPosition = 0;

    private void LoadExistingLogs()
    {
        if (string.IsNullOrEmpty(_logFilePath) || !File.Exists(_logFilePath)) return;
        try
        {
            var content = File.ReadAllText(_logFilePath);
            LogTextBox.AppendText(content);
            _lastReadPosition = new FileInfo(_logFilePath).Length;
            if (AutoScrollCheckBox.IsChecked == true)
                LogTextBox.ScrollToEnd();
        }
        catch { }
    }

    private void LoadNewLogContent()
    {
        if (string.IsNullOrEmpty(_logFilePath) || !File.Exists(_logFilePath)) return;
        try
        {
            var info = new FileInfo(_logFilePath);
            if (info.Length <= _lastReadPosition) return;

            using var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(_lastReadPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var newContent = reader.ReadToEnd();
            if (!string.IsNullOrEmpty(newContent))
            {
                LogTextBox.AppendText(newContent);
                _lastReadPosition = fs.Position;
                if (AutoScrollCheckBox.IsChecked == true)
                    LogTextBox.ScrollToEnd();
            }
        }
        catch { }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
        StatusText.Text = "已清空";
    }

    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        // 日志目录已迁移至 %LocalAppData%\SMTCPlayer\logs，经 Logger.LogFilePath 推导
        var logDir = Path.GetDirectoryName(Logger.LogFilePath) ?? "";
        if (!string.IsNullOrEmpty(logDir) && Directory.Exists(logDir))
            Process.Start(new ProcessStartInfo(logDir) { UseShellExecute = true });
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _refreshTimer.Stop();
        _logWatcher?.Dispose();
        _instance = null;
    }
}
