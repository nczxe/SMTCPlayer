using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SMTCPlayer.Core.Services;
using System.Text.RegularExpressions;

namespace SMTCPlayer.WinUI.Dialogs;

public sealed partial class PinSetupDialog : ContentDialog
{
    /// <summary>用户在"输入 PIN"框中填写的值。</summary>
    public string Pin => PinInput.Password;

    private static readonly Regex AllowedPin = new(@"^[A-Za-z0-9!@#$%^&*()\-_=+\[\]{};:'"",.<>?/|\\~`]{4,16}$",
        RegexOptions.Compiled);

    public PinSetupDialog()
    {
        InitializeComponent();
    }

    /// <summary>首次设置 PIN 模式（标题="设置访问 PIN"，取消按钮="跳过"）。</summary>
    public void UseInitialSetupMode()
    {
        Logger.Info("PinSetupDialog: 首次设置模式");
        Title = "设置访问 PIN";
        HintText.Text = "首次启动需要为远程访问设置 PIN，后续可随时在设置中修改。";
        NewPinLabel.Text = "PIN";
        CloseButtonText = "跳过";
    }

    /// <summary>修改 PIN 模式（标题="修改访问 PIN"，取消按钮="取消"）。服务端 reset_pin 无需旧 PIN。</summary>
    public void UseChangeMode()
    {
        Logger.Info("PinSetupDialog: 修改 PIN 模式");
        Title = "修改访问 PIN";
        HintText.Text = "修改远程访问 PIN。修改后移动端需要重新登录。";
        NewPinLabel.Text = "新 PIN";
        CloseButtonText = "取消";
    }

    // ============== 校验 ==============

    private void PinInput_PasswordChanged(object sender, RoutedEventArgs e) => Validate();
    private void PinConfirm_PasswordChanged(object sender, RoutedEventArgs e) => Validate();

    private void Validate()
    {
        ErrorText.Visibility = Visibility.Collapsed;

        if (string.IsNullOrEmpty(PinInput.Password))
        {
            IsPrimaryButtonEnabled = false;
            return;
        }

        if (!AllowedPin.IsMatch(PinInput.Password))
        {
            ShowError("PIN 必须是 4-16 位字母、数字或安全特殊字符");
            IsPrimaryButtonEnabled = false;
            return;
        }

        if (PinInput.Password != PinConfirm.Password)
        {
            ShowError("两次输入的 PIN 不一致");
            IsPrimaryButtonEnabled = false;
            return;
        }

        IsPrimaryButtonEnabled = true;
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    // ============== 事件 ==============

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Validate();
        if (!IsPrimaryButtonEnabled) args.Cancel = true;
    }
}
