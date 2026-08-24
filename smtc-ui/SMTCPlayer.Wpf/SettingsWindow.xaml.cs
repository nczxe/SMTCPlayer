using System.Windows;
using Microsoft.Win32;
using SMTCPlayer.Core.Services;

namespace SMTCPlayer.Wpf;

public partial class SettingsWindow : Window
{
    private const string AppName = "SMTCPlayer";
    private const string RegistryRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private bool _loading;

    public event Action? ExitRequested;
    public event Action<int>? SettingsSaved;

    public SettingsWindow(int effectivePort = 0)
    {
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
