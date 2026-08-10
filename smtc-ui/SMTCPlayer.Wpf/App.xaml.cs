using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using SMTCPlayer.Core.Services;

namespace SMTCPlayer.Wpf;

public partial class App : System.Windows.Application
{
    public App()
    {
        Logger.Init();
        Logger.Info("应用程序启动");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Apply(SMTCPlayer.Wpf.Properties.Settings.Default.Theme);
        new MainWindow().Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("UI 线程未处理异常", e.Exception);
        System.Windows.MessageBox.Show($"发生错误:\n{e.Exception.Message}\n\n日志文件:\n{Logger.LogFilePath}", "SMTC Player", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Logger.Error("AppDomain 未处理异常", ex);
            try { System.Windows.MessageBox.Show($"发生严重错误:\n{ex.Message}\n\n日志文件:\n{Logger.LogFilePath}", "SMTC Player", MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("未观察到的 Task 异常", e.Exception);
        e.SetObserved();
    }
}
