using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ClaudeSwitch.Core;

/// <summary>
/// Renews an account's short-lived access token from its refresh token — the exact OAuth
/// refresh-token grant Claude Code performs internally (reverse-engineered from the shipped
/// binary: POST platform.claude.com/v1/oauth/token, JSON body with grant_type/refresh_token/
/// client_id/scope, response access_token/refresh_token/expires_in/refresh_token_expires_in/scope).
///
/// This keeps inactive accounts alive. Access tokens last ~1 day, refresh tokens ~1 month, and
/// the refresh token ROTATES on every success — the response carries a NEW refresh token that
/// supersedes the old one server-side, so the rotated value must be saved atomically or the
/// account breaks. Reusing a superseded token returns invalid_grant and can invalidate the whole
/// token family, so refreshes must be serialized and never run concurrently on the same token.
///
/// Never call this for the ACTIVE account — Claude Code owns its rotation on disk.
/// </summary>
internal static class TokenRefresher
{
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    // Current host first, legacy hosts as fallback (an ongoing migration; same OAuth service).
    private static readonly string[] Endpoints =
    [
        "https://platform.claude.com/v1/oauth/token",
        "https://console.anthropic.com/v1/oauth/token",
        "https://api.anthropic.com/v1/oauth/token",
    ];

    private static readonly string[] DefaultScopes =
    [
        "user:profile", "user:inference", "user:sessions:claude_code",
        "user:mcp_servers", "user:file_upload",
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    internal enum Result { Refreshed, RefreshTokenDead, Unavailable }

    /// <summary>
    /// Refreshes the tokens inside <paramref name="credentialsJson"/> (a .credentials.json body).
    /// On success returns the updated JSON with the new access + rotated refresh token; the caller
    /// must persist it atomically, because the old refresh token is now dead.
    /// </summary>
    public static async Task<(Result Result, string? Updated)> RefreshAsync(
        string credentialsJson, CancellationToken token = default)
    {
        var refreshToken = ReadStringField(credentialsJson, "refreshToken");
        if (string.IsNullOrWhiteSpace(refreshToken))
            return (Result.RefreshTokenDead, null);

        var scope = string.Join(' ', ReadScopes(credentialsJson));

        var body = JsonSerializer.Serialize(new
        {
            grant_type = "refresh_token",
            refresh_token = refreshToken,
            client_id = ClientId,
            scope,
        });

        foreach (var endpoint in Endpoints)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };

                using var response = await Http.SendAsync(request, token);
                var json = await response.Content.ReadAsStringAsync(token);

                if (response.IsSuccessStatusCode)
                {
                    var updated = Merge(credentialsJson, json, refreshToken);
                    return updated is null ? (Result.Unavailable, null) : (Result.Refreshed, updated);
                }

                // invalid_grant is the OAuth signal that the refresh token is revoked/expired —
                // the only case that truly warrants re-signing in. A 404 means wrong host: try
                // the next endpoint. Anything else (429, 5xx) is temporary.
                if (json.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
                    return (Result.RefreshTokenDead, null);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    continue;

                return (Result.Unavailable, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Connection failure — try the next host before giving up.
            }
        }

        return (Result.Unavailable, null);
    }

    /// <summary>Writes the refreshed tokens from an OAuth response back into the credentials JSON.</summary>
    private static string? Merge(string credentialsJson, string tokenResponse, string oldRefreshToken)
    {
        try
        {
            using var resp = JsonDocument.Parse(tokenResponse);
            var root = resp.RootElement;

            var access = Str(root, "access_token");
            if (access is null) return null;   // not the shape we expect

            var refresh = Str(root, "refresh_token") ?? oldRefreshToken;   // rotation-aware
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var updated = credentialsJson;
            updated = SetString(updated, "accessToken", access);
            updated = SetString(updated, "refreshToken", refresh);

            if (root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt64(out var secs))
                updated = SetNumber(updated, "expiresAt", now + secs * 1000);

            // Only overwrite the refresh-token expiry when the server actually returned one;
            // otherwise keep the stored value (matches Claude Code's own behaviour).
            if (root.TryGetProperty("refresh_token_expires_in", out var rei) && rei.TryGetInt64(out var rsecs))
                updated = SetNumber(updated, "refreshTokenExpiresAt", now + rsecs * 1000);

            return updated;
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Str(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    // ── credential-field helpers (surgical edits, tolerant of duplicate keys) ────────────────

    private static string SetString(string json, string field, string value) =>
        PatchOauth(json, field, JsonSurgeon.ToJsonString(value));

    private static string SetNumber(string json, string field, long value) =>
        PatchOauth(json, field, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string PatchOauth(string json, string field, string rawValue)
    {
        var oauthRaw = JsonSurgeon.GetRawValue(json, "claudeAiOauth");
        if (oauthRaw is null) return json;
        var newOauth = JsonSurgeon.SetRawValue(oauthRaw, field, rawValue);
        return JsonSurgeon.SetRawValue(json, "claudeAiOauth", newOauth);
    }

    private static string? ReadStringField(string credentialsJson, string field)
    {
        var oauthRaw = JsonSurgeon.GetRawValue(credentialsJson, "claudeAiOauth");
        return oauthRaw is null ? null : JsonSurgeon.GetStringValue(oauthRaw, field);
    }

    private static IReadOnlyList<string> ReadScopes(string credentialsJson)
    {
        try
        {
            var oauthRaw = JsonSurgeon.GetRawValue(credentialsJson, "claudeAiOauth");
            if (oauthRaw is not null)
            {
                using var doc = JsonDocument.Parse(oauthRaw);
                if (doc.RootElement.TryGetProperty("scopes", out var s) && s.ValueKind == JsonValueKind.Array)
                {
                    var list = s.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToList();
                    if (list.Count > 0) return list;
                }
            }
        }
        catch (JsonException) { }

        return DefaultScopes;
    }
}
