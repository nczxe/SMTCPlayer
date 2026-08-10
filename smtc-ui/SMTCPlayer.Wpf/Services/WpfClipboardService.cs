using SMTCPlayer.Core.Services;

namespace SMTCPlayer.Wpf.Services;

public class WpfClipboardService : IClipboardService
{
    public void SetText(string text) => System.Windows.Clipboard.SetText(text);
}
