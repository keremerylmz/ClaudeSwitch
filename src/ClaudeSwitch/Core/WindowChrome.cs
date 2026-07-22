using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

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

    // Win11-only. Ignored (harmless non-zero return) on Win10, so no version check is needed —
    // the attributes simply do nothing there and the window keeps its flat opaque look.
    private const int DwmwaWindowCornerPreference = 33;   // 2 = rounded
    private const int DwmwaSystemBackdropType = 38;       // 2 = Mica
    private const int CornerRound = 2;
    private const int BackdropMica = 2;
    private const int BackdropNone = 1;

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

    /// <summary>
    /// Rounds the window's corners and, when <paramref name="mica"/> is on, paints the system Mica
    /// material behind it. Win11 only — the calls are silently ignored on Win10, where the caller's
    /// opaque background stays. The window itself must be transparent for Mica to show through, so
    /// that is the caller's responsibility (see <see cref="MicaHost"/>).
    /// </summary>
    public static void ApplyBackdrop(Window window, bool mica)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var corner = CornerRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

        var backdrop = mica ? BackdropMica : BackdropNone;
        DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

        // Without this WPF paints its own opaque surface and Mica shows through as solid black.
        // Clearing the composition target's background is what lets the DWM material appear. Only
        // touched for Mica; the opaque path keeps WPF's default so nothing changes on Windows 10.
        if (mica && HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
            target.BackgroundColor = Colors.Transparent;
    }

    /// <summary>Whether this Windows build supports the Mica material (Win11 22000+).</summary>
    public static bool SupportsMica =>
        Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;

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
