using System.Text.Json;

namespace ClaudeSwitch.Core;

/// <summary>
/// User preferences, persisted to %APPDATA%\ClaudeSwitch\settings.json.
///
/// Kept tiny and plain-text on purpose — these are cosmetic choices, nothing sensitive, and a
/// user should be able to read or reset them by hand.
/// </summary>
internal sealed class AppSettings
{
    /// <summary>
    /// Legacy two-state theme flag. Superseded by <see cref="ThemeMode"/>, but still written so
    /// downgrading to an older build keeps the user's choice, and still read on first load of a
    /// settings file written before ThemeMode existed.
    /// </summary>
    public bool DarkMode { get; set; }

    /// <summary>"light", "dark", or "system" to follow the Windows app theme.</summary>
    public string ThemeMode { get; set; } = "";

    /// <summary>
    /// Paint the window on the Windows 11 Mica material instead of a flat background. Ignored on
    /// Windows 10, which has no such material. Defaults on where supported — it's the single
    /// biggest "native and premium" cue on Win11.
    /// </summary>
    public bool Translucent { get; set; } = true;

    /// <summary>Ring each account's avatar with its live 5-hour usage (green → amber → red).</summary>
    public bool UsageRings { get; set; } = true;

    /// <summary>Mask every email and org name to ••••, for screenshots and screen-sharing.</summary>
    public bool Redact { get; set; }

    /// <summary>Compact mode hides the per-account usage panel for a denser list.</summary>
    public bool Compact { get; set; }

    /// <summary>Interface language code (e.g. "en", "tr"). Empty falls back to English.</summary>
    public string Language { get; set; } = "en";

    // ── behaviour ─────────────────────────────────────────────────────────────

    /// <summary>Automatically switch to the account with the most headroom when the active one hits its limit.</summary>
    public bool AutoSwitch { get; set; }

    /// <summary>5-hour utilization (%) at which auto-switch and the "at limit" warning trigger.</summary>
    public int AutoSwitchThreshold { get; set; } = 95;

    /// <summary>Show a tray notification as the active account nears its limit.</summary>
    public bool LimitNotifications { get; set; } = true;

    /// <summary>
    /// Show a balloon confirming each switch. Separate from <see cref="LimitNotifications"/>:
    /// the hotkey exists to switch WITHOUT looking at anything, and a toast per press defeats it.
    /// </summary>
    public bool SwitchNotifications { get; set; } = true;

    /// <summary>
    /// After a switch, name the Claude Code sessions that are open.
    ///
    /// Deliberately informational rather than a confirmation prompt: switching mid-conversation
    /// is the feature, not a hazard, so the useful thing to say is WHICH sessions will pick the
    /// new account up — not to stand between the user and the click they just made.
    /// </summary>
    public bool ShowLiveSessions { get; set; } = true;

    /// <summary>Left-clicking the tray icon opens the account menu (otherwise it needs a right-click).</summary>
    public bool TrayLeftClickMenu { get; set; } = true;

    /// <summary>Append each account's 5-hour usage to its row in the tray menu.</summary>
    public bool TrayMenuUsage { get; set; } = true;

    /// <summary>Account list order: "recent", "name", "free", or "plan".</summary>
    public string AccountSort { get; set; } = "recent";

    /// <summary>Launch on sign-in and sit in the tray.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Enable the global hotkey (Ctrl+Alt+S) that cycles to the next account.</summary>
    public bool GlobalHotkey { get; set; } = true;

    /// <summary>Check GitHub for a newer release on startup.</summary>
    public bool CheckForUpdates { get; set; } = true;

    // ── window placement ──────────────────────────────────────────────────────
    // Zero width means "never saved"; the window then falls back to centring itself.

    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }

    // ── mini mode ──────────────────────────────────────────────────────────────

    /// <summary>Show the small always-on-top usage pill instead of the full window.</summary>
    public bool MiniMode { get; set; }

    /// <summary>
    /// Last position of the mini pill; null until it has been placed once. Nullable, not NaN:
    /// System.Text.Json refuses to write NaN/Infinity, so a NaN here made every Save() throw and
    /// no setting persisted at all.
    /// </summary>
    public double? MiniLeft { get; set; }
    public double? MiniTop { get; set; }

    private static string Path => System.IO.Path.Combine(ClaudePaths.AppDataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();

                // Settings files written before ThemeMode existed only carry the DarkMode bool.
                if (string.IsNullOrWhiteSpace(loaded.ThemeMode))
                    loaded.ThemeMode = loaded.DarkMode ? "dark" : "light";

                return loaded;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Corrupt settings should never block startup; fall back to defaults.
        }
        return new AppSettings { ThemeMode = "light" };
    }

    public void Save()
    {
        try
        {
            // Keep the legacy flag truthful so an older build reads the right theme.
            DarkMode = ThemeManager.IsDark;

            ClaudePaths.EnsureAppDirectories();
            AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // Persisting preferences is never worth crashing over. A NaN in one field once made
            // JsonSerializer throw here, which — because every settings toggle calls Save() — took
            // the whole settings panel down with it. Whatever the cause, degrade to "not saved".
            CrashLog.Write("SettingsSave", ex);
        }
    }
}
