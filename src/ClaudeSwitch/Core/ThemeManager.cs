using System.Windows;

namespace ClaudeSwitch.Core;

/// <summary>
/// Swaps the active theme dictionary at runtime.
///
/// The theme sits at slot 0 of the application's merged dictionaries (see App.xaml). Replacing
/// that entry re-resolves every <c>DynamicResource</c> brush across all open windows instantly,
/// so dark mode toggles live with no window rebuild.
/// </summary>
internal static class ThemeManager
{
    public static bool IsDark { get; private set; }

    public static void Apply(bool dark)
    {
        IsDark = dark;

        var app = Application.Current;
        if (app is null) return;

        var uri = new Uri(dark ? "pack://application:,,,/Themes/Dark.xaml"
                               : "pack://application:,,,/Themes/Light.xaml");
        var theme = new ResourceDictionary { Source = uri };

        // Slot 0 is the theme by convention; replace it in place so nothing else is disturbed.
        var dicts = app.Resources.MergedDictionaries;
        if (dicts.Count == 0) dicts.Add(theme);
        else dicts[0] = theme;

        // Brushes are DynamicResource so they updated automatically; the native title bars are
        // not part of that, so retint them explicitly.
        WindowChrome.ApplyToAll(dark);
    }
}
