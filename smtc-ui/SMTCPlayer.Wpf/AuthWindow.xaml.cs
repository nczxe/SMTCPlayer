using System.Text.RegularExpressions;
using System.Windows;

namespace SMTCPlayer.Wpf;

public partial class AuthWindow : Window
{
    private static readonly Regex PinRegex =
        new(@"^[A-Za-z0-9!@#$%^&*()_\-+=\[\]{}:;,.?/|~]{4,16}$");

    private readonly bool _isSetup;

    public AuthWindow(bool isSetup)
    {
        InitializeComponent();
        _isSetup = isSetup;

        if (isSetup)
        {
            Title = "设置访问 PIN";
            TitleText.Text = "设置访问 PIN";
            SubtitleText.Text = "首次使用需要设置一个 4-16 位的访问 PIN，手机网页端登录时将使用该 PIN";
            ConfirmLabel.Visibility = Visibility.Visible;
            ConfirmBox.Visibility = Visibility.Visible;
        }
        else
        {
            Title = "输入访问 PIN";
            TitleText.Text = "输入访问 PIN";
            SubtitleText.Text = "请输入已设置的访问 PIN 以完成认证";
            ConfirmLabel.Visibility = Visibility.Collapsed;
            ConfirmBox.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) => PinBox.Focus();
    }

    public string Pin => PinBox.Password;

    public void ShowError(string message)
    {
        ErrorText.Text = message;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var pin = PinBox.Password;
        if (!PinRegex.IsMatch(pin))
        {
            ErrorText.Text = "PIN 需 4-16 位，可包含字母、数字和 !@#$%^&*()_-+=[]{}:;,.?/|~";
            return;
        }
        if (_isSetup && pin != ConfirmBox.Password)
        {
            ErrorText.Text = "两次输入的 PIN 不一致";
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
