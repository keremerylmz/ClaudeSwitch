using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

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
    /// <summary>Whether dark brushes are in effect right now — already resolved, never "system".</summary>
    public static bool IsDark { get; private set; }

    /// <summary>The mode the user chose: "light", "dark", or "system".</summary>
    public static string Mode { get; private set; } = "light";

    /// <summary>
    /// A small fixed palette used for per-account avatar colours. Not a theme accent — the app's
    /// own accent is the single terracotta the theme dictionaries ship; these are just labels a
    /// user can pin to an account so personal/work/client rows are scannable at a glance.
    /// </summary>
    internal readonly record struct AccentPalette(string Key, string Name, string Base);

    public static readonly AccentPalette[] Accents =
    [
        new("terracotta", "Terracotta", "#C96442"),
        new("blue",       "Blue",       "#3B72C4"),
        new("green",      "Green",      "#2F7A5B"),
        new("purple",     "Purple",     "#7A5AC4"),
        new("graphite",   "Graphite",   "#57544E"),
    ];

    /// <summary>Applies a resolved light/dark theme. Kept for callers that know the exact value.</summary>
    public static void Apply(bool dark) => ApplyResolved(dark);

    /// <summary>Applies a mode, resolving "system" against the current Windows app theme.</summary>
    public static void ApplyMode(string mode)
    {
        Mode = string.IsNullOrWhiteSpace(mode) ? "light" : mode;
        ApplyResolved(Resolve(Mode));
    }

    /// <summary>Re-resolves the current mode — used when Windows switches its own app theme.</summary>
    public static void Reapply() => ApplyResolved(Resolve(Mode));

    private static bool Resolve(string mode) => mode switch
    {
        "dark" => true,
        "light" => false,
        _ => WindowsPrefersDark(),
    };

    private static void ApplyResolved(bool dark)
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

    /// <summary>WinForms is referenced for the tray, so System.Drawing.Color is also in scope.</summary>
    public static System.Windows.Media.Color Parse(string hex)
        => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    /// <summary>
    /// Reads Windows' own light/dark preference for apps. Defaults to light when the value is
    /// missing, which is what every Windows build without the setting looks like.
    /// </summary>
    public static bool WindowsPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
