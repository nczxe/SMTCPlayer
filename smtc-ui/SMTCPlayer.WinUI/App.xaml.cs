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

        Logger.Info("=== WinUI 应用启动 ===");

        Dispatcher = DispatcherQueue.GetForCurrentThread();

        _mainWindow = new MainWindow();
        MainWindowInstance = _mainWindow;
        _mainWindow.Activate();
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
