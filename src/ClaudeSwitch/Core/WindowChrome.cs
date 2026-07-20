using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeSwitch.Core;

/// <summary>
/// Tints the native Windows title bar to match the theme.
///
/// WPF doesn't theme the OS title bar, so in dark mode the caption and its min/max/close buttons
/// stay bright white above a dark window. The DWM immersive-dark-mode attribute flips the whole
/// caption dark — the cleanest fix, using the real system buttons rather than reinventing them.
/// </summary>
internal static class WindowChrome
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE — 20 on current Windows, 19 on early Win10 20H1 builds.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeOld = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var flag = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref flag, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref flag, sizeof(int));

        // The caption only repaints on the next activation; nudge it so the change shows at once.
        if (window.IsVisible) Nudge(window);
    }

    private static void Nudge(Window window)
    {
        // A no-op resize forces the non-client area to redraw immediately.
        var w = window.Width;
        if (double.IsNaN(w)) return;
        window.Width = w + 0.1;
        window.Width = w;
    }

    /// <summary>Re-tints every open window — used when the theme is toggled live.</summary>
    public static void ApplyToAll(bool dark)
    {
        foreach (Window w in Application.Current.Windows) Apply(w, dark);
    }
}
