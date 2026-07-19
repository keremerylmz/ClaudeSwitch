using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeSwitch.Core;

/// <summary>
/// Closes the stray login tab Claude Code opens in the ordinary browser.
///
/// The CLI always launches the default browser and there is no flag or environment variable
/// that stops it (`--no-browser` does not exist in this version; CI / BROWSER / SSH_* were all
/// tried and ignored). That tab shows whichever account the browser is signed into — i.e. the
/// OLD one — so leaving it on screen invites the user to authorize exactly the wrong account.
///
/// Identifying it safely is the whole problem: matching on "a browser window whose title says
/// Claude" would happily close the user's real Claude chat. Instead we snapshot every browser
/// window title BEFORE the CLI runs and only touch a window whose title CHANGED into something
/// login-shaped afterwards. A window we never saw change is never touched.
/// </summary>
internal static class BrowserTabs
{
    private const int SwRestore = 9;
    private const int SwMaximize = 3;
    private const uint KeyEventKeyUp = 0x0002;
    private const byte VkControl = 0x11;
    private const byte VkW = 0x57;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);

    /// <summary>Process names we are willing to send keystrokes to.</summary>
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "brave", "msedge", "firefox", "zen", "vivaldi", "opera",
        "chromium", "thorium", "librewolf", "floorp", "waterfox", "browser", "palemoon",
    };

    /// <summary>Title fragments that indicate the OAuth page rather than ordinary browsing.</summary>
    private static readonly string[] LoginTitleHints =
    [
        "authorize", "oauth", "sign in", "log in", "giriş", "claude.com", "anthropic",
    ];

    private static Dictionary<IntPtr, string> SnapshotWindows()
    {
        var result = new Dictionary<IntPtr, string>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0) return true;

            string processName;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return true;
            }

            if (!BrowserProcesses.Contains(processName)) return true;

            var sb = new StringBuilder(512);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.Length > 0) result[hWnd] = title;

            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>Records the current browser window titles so changes can be detected later.</summary>
    public static Dictionary<IntPtr, string> Snapshot() => SnapshotWindows();

    /// <summary>
    /// Finds the real login window a just-launched browser opened.
    ///
    /// We cannot trust the handle of the process we started: Chromium's first process is often
    /// a launcher that spawns the real browser and then exits, taking its (empty) window handle
    /// with it. So instead we look for a browser window that did NOT exist before the launch
    /// and is large enough to be a real window — the size test discards the small transient
    /// popups Chromium throws up (a "translate this page?" bubble measured 327x61).
    /// </summary>
    public static IntPtr FindNewRealWindow(Dictionary<IntPtr, string> before)
    {
        IntPtr fallback = IntPtr.Zero;

        foreach (var (hWnd, title) in SnapshotWindows())
        {
            if (before.ContainsKey(hWnd)) continue;
            if (!IsRealWindow(hWnd)) continue;

            // A window already showing the login page is the surest match.
            if (LooksLikeLoginPage(title) || title.Contains("claude", StringComparison.OrdinalIgnoreCase))
                return hWnd;

            fallback = hWnd;   // otherwise the first real new browser window
        }

        return fallback;
    }

    /// <summary>
    /// Watches for the stray login tab Claude Code opens in an ALREADY-OPEN browser window and
    /// closes it with Ctrl+W.
    ///
    /// Only windows that existed in <paramref name="before"/> are ever considered. That single
    /// rule is what keeps this from closing the private window we opened ourselves: the stray
    /// tab lands in a pre-existing window (its title changes), while the private window is a
    /// brand-new one. An earlier version matched on "any browser window whose title looks like
    /// a login page" and promptly closed the private window instead.
    ///
    /// Consequence: if the browser was not already running, the stray tab gets its own new
    /// window and is deliberately left alone. Closing the wrong window is far worse than
    /// leaving an extra tab open.
    /// </summary>
    /// <returns>True if a tab was closed.</returns>
    public static async Task<bool> CloseStrayLoginTabAsync(
        Dictionary<IntPtr, string> before,
        TimeSpan timeout,
        CancellationToken token = default)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            await Task.Delay(300, token);

            foreach (var (hWnd, title) in SnapshotWindows())
            {
                // Never touch a window that did not exist before the login started.
                if (!before.TryGetValue(hWnd, out var old)) continue;

                // Unchanged windows are none of our business.
                if (old == title) continue;

                if (!LooksLikeLoginPage(title)) continue;

                if (CloseActiveTab(hWnd)) return true;
            }
        }

        return false;
    }

    private static bool LooksLikeLoginPage(string title) =>
        LoginTitleHints.Any(h => title.Contains(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>Focuses a window and sends Ctrl+W, closing only its active tab.</summary>
    private static bool CloseActiveTab(IntPtr hWnd)
    {
        try
        {
            ForceForeground(hWnd);
            Thread.Sleep(120);

            // Bail out if focus did not actually land on the target: sending Ctrl+W to
            // whatever else has focus could close something the user cares about.
            if (GetForegroundWindow() != hWnd) return false;

            keybd_event(VkControl, 0, 0, UIntPtr.Zero);
            keybd_event(VkW, 0, 0, UIntPtr.Zero);
            keybd_event(VkW, 0, KeyEventKeyUp, UIntPtr.Zero);
            keybd_event(VkControl, 0, KeyEventKeyUp, UIntPtr.Zero);

            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    private struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// Maximises a window.
    ///
    /// Needed even though the browser is launched with --start-maximized: in practice the
    /// window still came up occupying only part of the screen, so the flag alone cannot be
    /// relied on. Applied to the window handle we already know belongs to the browser we
    /// started, so there is nothing to misidentify.
    /// </summary>
    public static void Maximize(IntPtr hWnd)
    {
        if (hWnd != IntPtr.Zero) ShowWindow(hWnd, SwMaximize);
    }

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    /// <summary>True when the window is currently maximised.</summary>
    public static bool IsMaximized(IntPtr hWnd) => hWnd != IntPtr.Zero && IsZoomed(hWnd);

    /// <summary>
    /// True for something that is plausibly a real browser window rather than one of the small
    /// transient popups Chromium spawns — the "translate this page?" bubble measured 327x61 and
    /// was being mistaken for the browser window itself.
    /// </summary>
    public static bool IsRealWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd)) return false;
        if (!GetWindowRect(hWnd, out var rect)) return false;

        return rect.Right - rect.Left >= 400 && rect.Bottom - rect.Top >= 300;
    }

    /// <summary>
    /// Raises a window past Windows' foreground lock, which otherwise makes a plain
    /// SetForegroundWindow call from a background app fail silently.
    /// </summary>
    public static void ForceForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;

        ShowWindow(hWnd, SwRestore);

        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(hWnd, out _);
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);

        // Sharing an input queue with the current foreground window lifts the lock.
        var attachedForeground = foregroundThread != currentThread &&
                                 AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != currentThread &&
                             AttachThreadInput(currentThread, targetThread, true);

        SetForegroundWindow(hWnd);

        if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
        if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
    }

}
