using System.Windows;

namespace SMTCPlayer.Wpf;

public static class ThemeManager
{
    public const string Dark = "Dark";
    public const string Light = "Light";

    public static string Current { get; private set; } = Dark;

    public static void Apply(string theme)
    {
        var normalized = theme == Light ? Light : Dark;
        if (Current == normalized) return;
        Current = normalized;

        var app = System.Windows.Application.Current;
        if (app == null) return;

        var newDictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{normalized}Theme.xaml", UriKind.Absolute),
        };
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(newDictionary);
    }
}
