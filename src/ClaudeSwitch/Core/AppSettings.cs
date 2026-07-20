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
    public bool DarkMode { get; set; }

    /// <summary>Compact mode hides the per-account usage panel for a denser list.</summary>
    public bool Compact { get; set; }

    /// <summary>Interface language code (e.g. "en", "tr"). Empty falls back to English.</summary>
    public string Language { get; set; } = "en";

    private static string Path => System.IO.Path.Combine(ClaudePaths.AppDataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Corrupt settings should never block startup; fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            ClaudePaths.EnsureAppDirectories();
            AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
