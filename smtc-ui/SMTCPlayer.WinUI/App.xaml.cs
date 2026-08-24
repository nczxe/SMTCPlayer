using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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
        _instanceMutex = new Mutex(true, MutexName, out var createdNew);
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (!createdNew)
        {
            Logger.Info("检测到已有实例运行，激活其窗口后退出本次启动");
            _activateSignal.Set();
            Environment.Exit(0);
            return;
        }

        Logger.Info("=== WinUI 应用启动 ===");

        Dispatcher = DispatcherQueue.GetForCurrentThread();

        _mainWindow = new MainWindow();
        MainWindowInstance = _mainWindow;
        _mainWindow.Activate();

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
