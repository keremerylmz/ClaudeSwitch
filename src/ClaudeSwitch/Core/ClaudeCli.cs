using System.Diagnostics;

namespace ClaudeSwitch.Core;

/// <summary>
/// Locates and launches the Claude Code CLI.
///
/// We deliberately do not reimplement Anthropic's OAuth flow. Adding an account means running
/// the real <c>claude auth login</c> (see <see cref="LoginSession"/>) and capturing whatever
/// credentials it writes — which keeps this tool correct even when the login flow changes upstream.
/// </summary>
internal static class ClaudeCli
{
    private static string? _cachedVersion;
    private static bool _versionResolved;

    /// <summary>
    /// The installed CLI version (e.g. "2.1.215"), or null if it cannot be determined.
    /// Resolved once and cached — the usage endpoint needs it in the User-Agent, and shelling
    /// out to <c>claude --version</c> on every call would be wasteful.
    /// </summary>
    public static string? Version
    {
        get
        {
            if (_versionResolved) return _cachedVersion;
            _versionResolved = true;
            _cachedVersion = ResolveVersion();
            return _cachedVersion;
        }
    }

    private static string? ResolveVersion()
    {
        var exe = Resolve();
        if (exe is null) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000)) { try { process.Kill(); } catch { } return null; }

            // Output looks like "2.1.215 (Claude Code)"; take the leading version token.
            var match = System.Text.RegularExpressions.Regex.Match(output, @"\d+\.\d+\.\d+");
            return match.Success ? match.Value : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    /// <summary>Full path to a launchable claude entry point, or null when not installed.</summary>
    public static string? Resolve()
    {
        // npm global install — by far the most common layout on Windows.
        var npmDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");

        foreach (var candidate in new[] { "claude.cmd", "claude.exe", "claude.bat" })
        {
            var direct = Path.Combine(npmDir, candidate);
            if (File.Exists(direct)) return direct;
        }

        // Native installer / anything else that put claude on PATH.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in new[] { "claude.cmd", "claude.exe", "claude.bat" })
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), candidate);
                    if (File.Exists(full)) return full;
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry — skip it.
                }
            }
        }

        return null;
    }

    public static bool IsInstalled => Resolve() is not null;
}
