namespace ClaudeSwitch.Core;

/// <summary>
/// Resolves where Claude Code keeps its state on this machine.
///
/// Both the CLI and the VS Code extension read the same two files, which is what makes a
/// single switch cover both surfaces at once.
/// </summary>
internal static class ClaudePaths
{
    /// <summary>
    /// Honours CLAUDE_CONFIG_DIR when set, matching Claude Code's own resolution order.
    /// When it is set, BOTH files live inside that directory; otherwise the config file
    /// sits next to the .claude folder rather than inside it.
    /// </summary>
    private static string? ConfigDirOverride
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }

    private static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The ~/.claude directory (sessions, settings, credentials).</summary>
    public static string ClaudeHome =>
        ConfigDirOverride ?? Path.Combine(UserProfile, ".claude");

    /// <summary>OAuth tokens: accessToken, refreshToken, expiresAt, subscriptionType.</summary>
    public static string CredentialsFile =>
        Path.Combine(ClaudeHome, ".credentials.json");

    /// <summary>Account identity + caches: oauthAccount, userID, model/subscription caches.</summary>
    public static string ConfigFile =>
        ConfigDirOverride is { } dir
            ? Path.Combine(dir, ".claude.json")
            : Path.Combine(UserProfile, ".claude.json");

    /// <summary>Claude Code's own settings — statusLine, hooks, model, permissions.</summary>
    public static string ClaudeSettingsFile =>
        Path.Combine(ClaudeHome, "settings.json");

    /// <summary>One &lt;pid&gt;.json per running Claude Code session.</summary>
    public static string SessionsDir =>
        Path.Combine(ClaudeHome, "sessions");

    /// <summary>Our own state: %APPDATA%\ClaudeSwitch.</summary>
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeSwitch");

    public static string ProfilesDir => Path.Combine(AppDataDir, "profiles");

    /// <summary>Safety net: every switch snapshots the outgoing files here first.</summary>
    public static string BackupsDir => Path.Combine(AppDataDir, "backups");

    public static string SettingsFile => Path.Combine(AppDataDir, "settings.json");

    /// <summary>
    /// Where Claude Code keeps its two files when pointed at <paramref name="configDir"/> via
    /// CLAUDE_CONFIG_DIR. Used to sign a new account in without disturbing the live one:
    /// the login runs against a throwaway directory, and nothing in the real one is touched.
    /// </summary>
    public static (string Credentials, string Config) FilesIn(string configDir) =>
        (Path.Combine(configDir, ".credentials.json"),
         Path.Combine(configDir, ".claude.json"));

    /// <summary>A fresh scratch directory for one isolated login.</summary>
    public static string CreateScratchConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"claudeswitch-login-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void EnsureAppDirectories()
    {
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(BackupsDir);
    }

    /// <summary>True when Claude Code has been set up and logged in at least once.</summary>
    public static bool ClaudeCodeIsInstalled =>
        File.Exists(ConfigFile) || Directory.Exists(ClaudeHome);
}
