using System.Windows;
using Microsoft.Win32;
using SMTCPlayer.Core.Services;

namespace SMTCPlayer.Wpf;

public partial class SettingsWindow : Window
{
    private const string AppName = "SMTCPlayer";
    private const string RegistryRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private readonly int _effectivePort;
    private bool _loading;

    public event Action? ExitRequested;
    public event Action<int>? SettingsSaved;

    public SettingsWindow(int effectivePort = 0)
    {
        _effectivePort = effectivePort > 0 ? effectivePort : Properties.Settings.Default.Port;
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        _loading = true;
        PortTextBox.Text = Properties.Settings.Default.Port.ToString();
        AutoStartCheckBox.IsChecked = Properties.Settings.Default.AutoStart;
        MinimizeOnStartCheckBox.IsChecked = Properties.Settings.Default.MinimizeOnStart;
        MinimizeOnCloseCheckBox.IsChecked = Properties.Settings.Default.MinimizeOnClose;
        DebugModeCheckBox.IsChecked = Properties.Settings.Default.DebugMode;
        DarkModeCheckBox.IsChecked = Properties.Settings.Default.Theme != ThemeManager.Light;
        _loading = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        int savedPort = Properties.Settings.Default.Port;
        if (int.TryParse(PortTextBox.Text, out var port) && port > 0 && port < 65536)
        {
            Properties.Settings.Default.Port = port;
            savedPort = port;
        }

        Properties.Settings.Default.AutoStart = AutoStartCheckBox.IsChecked ?? false;
        Properties.Settings.Default.MinimizeOnStart = MinimizeOnStartCheckBox.IsChecked ?? false;
        Properties.Settings.Default.MinimizeOnClose = MinimizeOnCloseCheckBox.IsChecked ?? false;
        Properties.Settings.Default.DebugMode = DebugModeCheckBox.IsChecked ?? false;
        Properties.Settings.Default.Save();

        UpdateAutoStart(Properties.Settings.Default.AutoStart);
        SettingsSaved?.Invoke(savedPort);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void DebugMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var enabled = DebugModeCheckBox.IsChecked ?? false;
        Properties.Settings.Default.DebugMode = enabled;
        Properties.Settings.Default.Save();

        if (enabled)
        {
            DebugWindow.Open(Owner ?? this);
        }
        else
        {
            DebugWindow.CloseAll();
        }
    }

    private void DarkMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var theme = DarkModeCheckBox.IsChecked == true
            ? ThemeManager.Dark
            : ThemeManager.Light;
        ThemeManager.Apply(theme);
        Properties.Settings.Default.Theme = theme;
        Properties.Settings.Default.Save();
        Logger.Info($"主题已切换为 {theme}");
    }

    private async void ClearCookies_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "确定清除网易云音乐 Cookie？\n清除后需要重新登录。", "SMTC Player",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        ClearCookiesButton.IsEnabled = false;
        try
        {
            var api = new SmtcApiClient("127.0.0.1", _effectivePort);
            var success = await api.ClearNcmCookiesAsync();
            if (success)
                System.Windows.MessageBox.Show("Cookie 已清除", "SMTC Player", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                System.Windows.MessageBox.Show("清除失败，请确认服务已启动", "SMTC Player", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"清除失败: {ex.Message}", "SMTC Player", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ClearCookiesButton.IsEnabled = true;
        }
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "退出 SMTC Player？", "SMTC Player",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        ExitRequested?.Invoke();
    }

    private static void UpdateAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true);
        if (key == null) return;

        if (enable)
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath != null)
            {
                key.SetValue(AppName, exePath);
            }
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }
}
