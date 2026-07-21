using System.Net.Http;
using System.Text.Json;

namespace ClaudeSwitch.Core;

/// <summary>
/// Checks GitHub for a newer release. Notify-only — it never downloads or replaces the running
/// exe (self-replacement is fragile and a security-sensitive thing to do silently); it points the
/// user at the release page and lets them choose.
/// </summary>
internal static class UpdateChecker
{
    private const string LatestApi = "https://api.github.com/repos/keremerylmz/ClaudeSwitch/releases/latest";
    public const string ReleasesPage = "https://github.com/keremerylmz/ClaudeSwitch/releases/latest";

    /// <summary>Returns the newer version tag (e.g. "v0.2.0") if one exists, else null.</summary>
    public static async Task<string?> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeSwitch-update-check");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            var json = await http.GetStringAsync(LatestApi);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tag)) return null;

            var latest = tag.GetString();
            if (string.IsNullOrWhiteSpace(latest)) return null;

            return IsNewer(latest, Current) ? latest : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;   // offline or rate-limited: silently skip
        }
    }

    private static string Current =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Compares "v0.2.0" / "0.2.0" style versions; true when <paramref name="latest"/> is greater.</summary>
    private static bool IsNewer(string latest, string current)
    {
        static int[] Parts(string v)
        {
            var trimmed = v.TrimStart('v', 'V');
            var bits = trimmed.Split('.', '-')[0].Split('.');
            var nums = new int[3];
            for (var i = 0; i < 3 && i < bits.Length; i++) int.TryParse(bits[i], out nums[i]);
            return nums;
        }

        var a = Parts(latest);
        var b = Parts(current);
        for (var i = 0; i < 3; i++)
        {
            if (a[i] > b[i]) return true;
            if (a[i] < b[i]) return false;
        }
        return false;
    }
}
