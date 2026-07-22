using System.Net.Http;
using System.Text.Json;

namespace ClaudeSwitch.Core;

/// <summary>
/// Asks GitHub whether a newer release exists and, if so, which asset this build should take.
/// Downloading and applying it is <see cref="Updater"/>'s job.
/// </summary>
internal static class UpdateChecker
{
    private const string LatestApi = "https://api.github.com/repos/keremerylmz/ClaudeSwitch/releases/latest";
    public const string ReleasesPage = "https://github.com/keremerylmz/ClaudeSwitch/releases/latest";

    /// <summary>Checksums file published alongside the binaries, used to verify a download.</summary>
    private const string ChecksumAsset = "SHA256SUMS.txt";

    /// <summary>
    /// A newer release and the one asset that matches this build.
    /// <paramref name="Sha256"/> is null when the release predates checksum publishing.
    /// </summary>
    internal sealed record Release(string Tag, string AssetName, string DownloadUrl, long Size, string? Sha256);

    /// <summary>
    /// Which asset this build must take. A framework-dependent ("lite") install would be broken
    /// by the self-contained binary and vice versa, so it is decided at compile time rather than
    /// guessed from the file name — users rename exes.
    /// </summary>
    public const string AssetForThisBuild =
#if SELF_CONTAINED
        "ClaudeSwitch.exe";
#else
        "ClaudeSwitch-lite.exe";
#endif

    /// <summary>The newer release, or null when up to date, offline, or rate-limited.</summary>
    public static async Task<Release?> CheckAsync()
    {
        try
        {
            using var http = NewClient();

            var json = await http.GetStringAsync(LatestApi);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            var tag = tagEl.GetString();
            if (string.IsNullOrWhiteSpace(tag) || !IsNewer(tag, Current)) return null;

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            string? url = null, checksumsUrl = null;
            long size = 0;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var link = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name is null || link is null) continue;

                if (string.Equals(name, AssetForThisBuild, StringComparison.OrdinalIgnoreCase))
                {
                    url = link;
                    size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                }
                else if (string.Equals(name, ChecksumAsset, StringComparison.OrdinalIgnoreCase))
                {
                    checksumsUrl = link;
                }
            }

            if (url is null) return null;   // release exists but has nothing this build can use

            var sha = checksumsUrl is null ? null : await ReadChecksumAsync(http, checksumsUrl, AssetForThisBuild);
            return new Release(tag, AssetForThisBuild, url, size, sha);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;   // offline or rate-limited: silently skip
        }
    }

    internal static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeSwitch-updater");
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return http;
    }

    /// <summary>Pulls one "&lt;sha256&gt;  &lt;filename&gt;" line out of the checksums file.</summary>
    private static async Task<string?> ReadChecksumAsync(HttpClient http, string url, string assetName)
    {
        try
        {
            var text = await http.GetStringAsync(url);
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                // sha256sum writes "*name" for binary mode; accept either form.
                if (string.Equals(parts[^1].TrimStart('*'), assetName, StringComparison.OrdinalIgnoreCase))
                    return parts[0];
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
        return null;
    }

    public static string Current =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Compares "v0.3.1" / "0.3.1" style versions; true when <paramref name="latest"/> is greater.</summary>
    internal static bool IsNewer(string latest, string current)
    {
        static int[] Parts(string v)
        {
            // Strip a leading "v" and any "-beta" style suffix, THEN split into components.
            // Splitting on '.' and '-' together and taking [0] would leave only the major
            // number, which silently made every minor and patch release look like no update.
            var trimmed = v.TrimStart('v', 'V').Split('-', '+')[0];
            var bits = trimmed.Split('.');

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
