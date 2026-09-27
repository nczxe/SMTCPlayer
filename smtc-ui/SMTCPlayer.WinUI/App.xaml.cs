using System.IO;
using System.IO.Pipes;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SMTCPlayer.Core.LanProtocol;
using SMTCPlayer.Core.Services;

namespace SMTCPlayer.WinUI;

/// <summary>
/// 应用程序入口与生命周期管理。
/// </summary>
public partial class App : Application
{
    private MainWindow? _mainWindow;
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _activateSignal;

    /// <summary>单实例 URI 转发所用的命名管道名（本机）。</summary>
    private const string UriPipeName = "SMTCPlayer.WinUI.UriPipe";

    public App()
    {
        // 在任何业务逻辑之前注册全局异常处理器，保证未捕获异常也能落到日志文件
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        InitializeComponent();

        // WinUI 自带的未处理异常事件
        UnhandledException += OnWinUIUnhandledException;
    }

    /// <summary>
    /// 应用主窗口实例（供全局访问）。
    /// </summary>
    public static MainWindow MainWindowInstance { get; private set; } = null!;

    /// <summary>
    /// 当前 DispatcherQueue，便于后台线程切回 UI。
    /// </summary>
    public static DispatcherQueue Dispatcher { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 初始化日志（写入 logs/ 目录）
        try { Logger.Init(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Logger 初始化失败：{ex}"); }

        // 单实例守卫：重复启动时激活既有实例后立即退出，
        // 避免多套 服务端/监视器 子进程并存与托盘实例堆积
        const string MutexName = @"Local\SMTCPlayer.WinUI.Instance";
        const string EventName = @"Local\SMTCPlayer.WinUI.Activate";
        var launchUri = ExtractLaunchUri();
        _instanceMutex = new Mutex(true, MutexName, out var createdNew);
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (!createdNew)
        {
            // 第二实例：优先经命名管道把 URI 交给既有实例执行；失败再退回"置前窗口"信号
            var forwarded = launchUri != null && TryForwardUriToExistingInstance(launchUri);
            if (!forwarded)
            {
                Logger.Info("检测到已有实例运行，激活其窗口后退出本次启动");
                _activateSignal.Set();
            }
            Environment.Exit(0);
            return;
        }

        Logger.Info("=== WinUI 应用启动 ===");

        Dispatcher = DispatcherQueue.GetForCurrentThread();

        _mainWindow = new MainWindow();
        MainWindowInstance = _mainWindow;
        _mainWindow.Activate();

        // 接收后续实例经命名管道转发的 URI 命令
        StartUriPipeServer();

        // "应用未运行"路径：本次启动携带的 URI 命令在后端就绪后执行
        if (launchUri != null)
            HandleLaunchUri(launchUri);

        // 后台监听"激活"信号：把已有实例从托盘唤起
        var signalThread = new Thread(() =>
        {
            while (_activateSignal.WaitOne())
            {
                Dispatcher.TryEnqueue(() =>
                {
                    try { MainWindowInstance?.BringToFrontFromExternalLaunch(); }
                    catch (Exception ex) { Logger.Warn($"激活既有实例失败: {ex.Message}"); }
                });
            }
        })
        { IsBackground = true, Name = "single-instance-activate" };
        signalThread.Start();
    }

    /// <summary>从命令行参数中提取 <c>smtcplayer://</c> URI（协议唤起时由系统作为参数传入）。</summary>
    private static string? ExtractLaunchUri()
    {
        var prefix = UriSchemeRegistrar.SchemeName + ":";
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (string.IsNullOrEmpty(arg)) continue;
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return arg;
        }
        return null;
    }

    /// <summary>把 URI 转发给既有实例（命名管道，带重试以等待对方管道就绪）。</summary>
    private static bool TryForwardUriToExistingInstance(string uri)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", UriPipeName, PipeDirection.Out);
                client.Connect(500);
                using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine(uri);
                Logger.Info("已通过命名管道把 URI 转发给既有实例");
                return true;
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    Logger.Warn($"转发 URI 到既有实例失败，改走激活信号: {ex.Message}");
                    return false;
                }
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>启动命名管道监听循环，接收后续实例转发的 URI。</summary>
    private void StartUriPipeServer()
    {
        var thread = new Thread(UriPipeLoop) { IsBackground = true, Name = "uri-pipe-server" };
        thread.Start();
    }

    private void UriPipeLoop()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    UriPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                server.WaitForConnection();

                using var reader = new StreamReader(server, Encoding.UTF8);
                var uri = reader.ReadLine();
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    var captured = uri!;
                    Dispatcher.TryEnqueue(() => HandleLaunchUri(captured));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"URI 管道监听异常: {ex.Message}");
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>把 URI 交给主窗口的处理器（须在 UI 线程调用）。</summary>
    private static void HandleLaunchUri(string uri)
    {
        try { MainWindowInstance?.EnqueueUri(uri); }
        catch (Exception ex) { Logger.Warn($"处理 URI 命令失败: {ex.Message}"); }
    }

    private static void OnWinUIUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Logger.Error("WinUI 未处理异常", e.Exception);
        e.Handled = true; // 阻止应用立即崩溃，让用户能保存状态
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Logger.Error("AppDomain 未处理异常", ex);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("未观察到的 Task 异常", e.Exception);
        e.SetObserved();
    }
}
