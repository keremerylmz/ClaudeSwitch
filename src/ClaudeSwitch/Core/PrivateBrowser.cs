using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeSwitch.Core;

/// <summary>
/// Opens a URL in a private/incognito window.
///
/// This is the only lever that actually solves "the login page keeps authorizing my old
/// account". CLAUDE_CONFIG_DIR isolates the local credential files, but the consent page
/// follows the BROWSER's claude.ai session — and that page offers no way to switch accounts.
/// A private window carries no session, so claude.ai has to ask who you are.
///
/// The user's default browser is tried first; only if it is unknown do we fall back to
/// scanning for anything we recognise. Hard-coding one browser would strand anyone whose
/// machine is set up differently.
/// </summary>
internal static class PrivateBrowser
{
    /// <summary>How to open a session-less window, keyed by executable name.</summary>
    private static readonly Dictionary<string, (string Name, string Flag)> KnownBrowsers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Chromium family
            ["chrome.exe"]    = ("Chrome", "--incognito"),
            ["brave.exe"]     = ("Brave", "--incognito"),
            ["vivaldi.exe"]   = ("Vivaldi", "--incognito"),
            ["chromium.exe"]  = ("Chromium", "--incognito"),
            ["thorium.exe"]   = ("Thorium", "--incognito"),
            ["browser.exe"]   = ("Yandex", "--incognito"),
            ["ungoogled-chromium.exe"] = ("Chromium", "--incognito"),

            // Edge uses its own spelling
            ["msedge.exe"]    = ("Edge", "--inprivate"),

            // Opera family
            ["opera.exe"]     = ("Opera", "--private"),
            ["opera_gx.exe"]  = ("Opera GX", "--private"),
            ["launcher.exe"]  = ("Opera", "--private"),

            // Firefox family (Zen, Floorp, LibreWolf and friends are all Firefox forks)
            ["firefox.exe"]   = ("Firefox", "-private-window"),
            ["zen.exe"]       = ("Zen", "-private-window"),
            ["floorp.exe"]    = ("Floorp", "-private-window"),
            ["librewolf.exe"] = ("LibreWolf", "-private-window"),
            ["waterfox.exe"]  = ("Waterfox", "-private-window"),
            ["palemoon.exe"]  = ("Pale Moon", "-private-window"),
            ["tor.exe"]       = ("Tor", "-private-window"),
        };

    /// <summary>Where to look when the default browser is not one we recognise.</summary>
    private static readonly string[] ScanRelativePaths =
    [
        @"BraveSoftware\Brave-Browser\Application\brave.exe",
        @"Google\Chrome\Application\chrome.exe",
        @"Microsoft\Edge\Application\msedge.exe",
        @"Mozilla Firefox\firefox.exe",
        @"Zen Browser\zen.exe",
        @"Vivaldi\Application\vivaldi.exe",
        @"Opera\opera.exe",
        @"Opera GX\opera.exe",
        @"LibreWolf\librewolf.exe",
        @"Floorp\floorp.exe",
        @"Waterfox\waterfox.exe",
        @"Chromium\Application\chrome.exe",
    ];

    internal readonly record struct Browser(string Name, string Path, string Flag, bool IsDefault);

    // ── discovery ───────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the browser Windows uses for https, via the UserChoice association.
    /// Returns null when there is none or it is not one we know how to open privately.
    /// </summary>
    private static Browser? DefaultBrowser()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");

            if (choice?.GetValue("ProgId") is not string progId || string.IsNullOrWhiteSpace(progId))
                return null;

            using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            if (command?.GetValue(null) is not string raw || string.IsNullOrWhiteSpace(raw))
                return null;

            var exe = ExtractExecutable(raw);
            if (exe is null || !File.Exists(exe)) return null;

            var fileName = Path.GetFileName(exe);
            if (!KnownBrowsers.TryGetValue(fileName, out var known)) return null;

            return new Browser(known.Name, exe, known.Flag, IsDefault: true);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>Pulls the program path out of a registry shell command string.</summary>
    private static string? ExtractExecutable(string command)
    {
        command = command.Trim();

        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        // Unquoted: the path runs up to the first argument, which conventionally starts with -/%.
        var space = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return space > 0 ? command[..(space + 4)] : null;
    }

    private static IEnumerable<string> ProgramRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // Chrome, Brave and most Firefox forks frequently install per-user.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Programs");
        yield return local;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    }

    /// <summary>Any installed browser we recognise, ignoring which one is default.</summary>
    private static Browser? ScanForAny()
    {
        foreach (var relative in ScanRelativePaths)
        {
            foreach (var root in ProgramRoots())
            {
                if (string.IsNullOrEmpty(root)) continue;

                var full = Path.Combine(root, relative);
                if (!File.Exists(full)) continue;

                var fileName = Path.GetFileName(full);
                if (KnownBrowsers.TryGetValue(fileName, out var known))
                    return new Browser(known.Name, full, known.Flag, IsDefault: false);
            }
        }

        return null;
    }

    /// <summary>The browser we would use: the default when recognised, otherwise any we find.</summary>
    public static Browser? Find() => DefaultBrowser() ?? ScanForAny();

    // ── launching ───────────────────────────────────────────────────────────

    /// <summary>A launched session-free browser window and the throwaway profile behind it.</summary>
    internal readonly record struct Session(Browser Browser, string ProfileDir);

    /// <summary>
    /// Opens <paramref name="url"/> in a brand-new browser instance backed by an empty,
    /// throwaway profile directory.
    ///
    /// Why not simply pass --incognito? Because when the browser is already running, that
    /// invocation is handed to the existing instance, which may reuse an open private window
    /// and silently drops window-sizing flags. Measured: no new window appeared at all, so
    /// nothing could be found, maximised or focused — the window just stayed small.
    ///
    /// A separate profile cannot be folded into the running instance. The window is therefore
    /// always new, always starts maximised, and has no claude.ai cookies whatsoever — which is
    /// a stronger guarantee than private mode gives us anyway. It also comes up focused on top,
    /// so nothing has to fight the user for foreground afterwards.
    /// </summary>
    public static Session? Open(string url)
    {
        if (Find() is not { } browser) return null;

        var profileDir = Path.Combine(Path.GetTempPath(), $"claudeswitch-browser-{Guid.NewGuid():n}");

        try
        {
            Directory.CreateDirectory(profileDir);

            var isFirefoxFamily = browser.Flag == "-private-window";

            var arguments = isFirefoxFamily
                ? $"-no-remote -profile \"{profileDir}\" -private-window \"{url}\""
                : $"--user-data-dir=\"{profileDir}\" --no-first-run --no-default-browser-check " +
                  $"--start-maximized \"{url}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = browser.Path,
                Arguments = arguments,
                UseShellExecute = false,
            });

            return new Session(browser, profileDir);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            CleanUpProfile(profileDir);
            return null;
        }
    }

    /// <summary>
    /// Deletes throwaway browser profiles left behind by earlier runs.
    ///
    /// Cleanup right after a login usually fails because the browser still has the profile
    /// open, so this runs at startup when those files are long since released. Profiles are
    /// only a few MB each, but they should not accumulate in %TEMP% forever.
    /// </summary>
    public static void SweepOldProfiles()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(
                         Path.GetTempPath(), "claudeswitch-browser-*"))
            {
                CleanUpProfile(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes a throwaway browser profile once the login is over.</summary>
    public static void CleanUpProfile(string? profileDir)
    {
        if (string.IsNullOrEmpty(profileDir)) return;

        try
        {
            if (Directory.Exists(profileDir)) Directory.Delete(profileDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The browser may still hold a lock; a leftover temp profile is harmless.
        }
    }

    /// <summary>
    /// Maximises the login window and brings it to the front, once it appears.
    ///
    /// The window is located by comparing against <paramref name="windowsBefore"/> rather than
    /// through the launched process: Chromium's initial process is frequently a launcher that
    /// exits after spawning the real browser, so its window handle is useless. --start-maximized
    /// was also observed to be ignored (the window opened filling half the screen), so the size
    /// is forced here on the handle we actually find.
    ///
    /// Runs exactly once and then stops: repeatedly re-raising a window fights the user for
    /// control of their own desktop.
    /// </summary>
    public static async Task FocusOnceAsync(
        Dictionary<IntPtr, string> windowsBefore, CancellationToken token = default)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            var hWnd = BrowserTabs.FindNewRealWindow(windowsBefore);
            if (hWnd != IntPtr.Zero)
            {
                BrowserTabs.ForceForeground(hWnd);

                // The browser finalises its own window bounds shortly AFTER the window first
                // appears, so a single early maximise gets overwritten. Re-apply for a couple
                // of seconds — bounded so it never turns into a fight for control — and stop as
                // soon as the window actually reports maximised.
                for (var i = 0; i < 8; i++)
                {
                    BrowserTabs.Maximize(hWnd);
                    try { await Task.Delay(250, token); } catch (TaskCanceledException) { return; }
                    if (BrowserTabs.IsMaximized(hWnd)) return;
                }

                return;
            }

            try { await Task.Delay(350, token); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>Opens a URL in whatever the default browser is — last-resort fallback.</summary>
    public static void OpenDefault(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
        }
    }

}
