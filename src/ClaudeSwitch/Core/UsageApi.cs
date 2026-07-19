using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeSwitch.Core;

/// <summary>Real subscription usage for one account, as the server reports it.</summary>
internal sealed class UsageSnapshot
{
    /// <summary>5-hour rolling window, 0–100.</summary>
    public double FiveHourPercent { get; init; }
    public DateTimeOffset? FiveHourResetsAt { get; init; }

    /// <summary>Weekly window, 0–100.</summary>
    public double SevenDayPercent { get; init; }
    public DateTimeOffset? SevenDayResetsAt { get; init; }

    /// <summary>When this snapshot was taken (local clock), for an "as of" label.</summary>
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Reads a Claude subscription's own usage from the same endpoint Claude Code's <c>/usage</c>
/// command uses.
///
/// This endpoint is UNDOCUMENTED. It was confirmed three ways — strings inside the Claude Code
/// binary, third-party tools that call it, and a live call from this project against a real
/// account (which also settled that <c>utilization</c> is a 0–100 percentage, not 0–1). Because
/// it is unofficial it can change or disappear without notice, so every failure path here
/// returns null and the UI shows "unavailable" rather than a stale or invented number.
///
/// It also rate-limits harshly with no Retry-After, so callers must cache and poll sparingly.
/// </summary>
internal static class UsageApi
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthBeta = "oauth-2025-04-20";

    // A single shared client; the User-Agent is mandatory or the endpoint 429s immediately.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // Must look like Claude Code. Version is resolved once at startup; a wrong-but-plausible
        // fallback is better than no header at all.
        var version = ClaudeCli.Version ?? "2.1.0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"claude-code/{version}");
        client.DefaultRequestHeaders.Add("anthropic-beta", OAuthBeta);

        return client;
    }

    /// <summary>
    /// Fetches usage for the account holding <paramref name="accessToken"/>.
    /// Returns null on any failure — expired token (401), rate limit (429), network error, or
    /// an unexpected response shape.
    /// </summary>
    public static async Task<UsageSnapshot?> FetchAsync(string accessToken, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await Http.SendAsync(request, token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
                return null;
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(token);
            return Parse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Extracts the OAuth access token from a .credentials.json body.</summary>
    public static string? ExtractAccessToken(string credentialsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(credentialsJson);
            var root = doc.RootElement;

            // Current layout nests everything under claudeAiOauth; older layouts were flat.
            if (root.TryGetProperty("claudeAiOauth", out var oauth) &&
                oauth.TryGetProperty("accessToken", out var nested))
                return nested.GetString();

            return root.TryGetProperty("accessToken", out var flat) ? flat.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static UsageSnapshot? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var five = Window(root, "five_hour");
        var seven = Window(root, "seven_day");
        if (five is null && seven is null) return null;   // not the shape we expect

        return new UsageSnapshot
        {
            FiveHourPercent = five?.Percent ?? 0,
            FiveHourResetsAt = five?.ResetsAt,
            SevenDayPercent = seven?.Percent ?? 0,
            SevenDayResetsAt = seven?.ResetsAt,
        };
    }

    private sealed record Win(double Percent, DateTimeOffset? ResetsAt);

    private static Win? Window(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object)
            return null;

        double percent = 0;
        if (w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number)
            percent = u.GetDouble();

        DateTimeOffset? resets = null;
        if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(r.GetString(), out var parsed))
            resets = parsed;

        return new Win(percent, resets);
    }
}
