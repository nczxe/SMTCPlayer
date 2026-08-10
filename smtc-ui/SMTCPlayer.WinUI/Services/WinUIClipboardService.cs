using SMTCPlayer.Core.Services;
using Windows.ApplicationModel.DataTransfer;

namespace SMTCPlayer.WinUI.Services;

/// <summary>
/// WinUI3 平台的剪贴板服务实现。
/// 使用 Windows.ApplicationModel.DataTransfer.Clipboard 写入文本。
/// </summary>
public sealed class WinUIClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var package = new DataPackage();
        package.SetText(text);
        try
        {
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch
        {
            // 剪贴板访问偶发失败（如被其他进程锁定），忽略即可
        }
    }
}
