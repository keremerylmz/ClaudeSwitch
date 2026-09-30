using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeSwitch.Core;

/// <summary>
/// Brings one of our windows to the front from the background.
///
/// Windows refuses SetForegroundWindow to a process that isn't the one the user last touched —
/// a plain Activate() just flashes the taskbar button. After a sign-in finishes in the browser,
/// though, coming back to the app IS the next step, so sharing the foreground window's input
/// queue for a moment lifts the lock for that one call.
/// </summary>
internal static class WindowFocus
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public static void Raise(Window window)
    {
        if (!window.IsVisible) window.Show();

        var hWnd = new WindowInteropHelper(window).Handle;
        if (hWnd == IntPtr.Zero) { window.Activate(); return; }

        if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);

        var foreground = GetForegroundWindow();
        var current = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);

        var attached = foregroundThread != 0 && foregroundThread != current &&
                       AttachThreadInput(current, foregroundThread, true);

        SetForegroundWindow(hWnd);
        window.Activate();

        if (attached) AttachThreadInput(current, foregroundThread, false);
    }
}
