using System.Text.Json.Serialization;

namespace ClaudeSwitch.Core;

/// <summary>
/// Non-secret description of a saved account. Written as plain JSON so a user can see
/// exactly what the app stores about them without decrypting anything.
/// </summary>
internal sealed class Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>User-editable display name; defaults to the account email.</summary>
    public string Label { get; set; } = "";

    public string Email { get; set; } = "";
    public string OrganizationName { get; set; } = "";

    /// <summary>"max" / "pro" / "team" / "enterprise" — drives the badge in the UI.</summary>
    public string SubscriptionType { get; set; } = "";

    public string SeatTier { get; set; } = "";
    public string AccountUuid { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Unix ms token expiry, mirrored from the credentials for display only.</summary>
    public long ExpiresAt { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Email : Label;

    [JsonIgnore]
    public bool TokenExpired =>
        ExpiresAt > 0 && DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAt) <= DateTimeOffset.UtcNow;

    /// <summary>
    /// An expired access token is not fatal — Claude Code silently refreshes it using the
    /// refresh token. Only a dead refresh token forces a real re-login, and we cannot see
    /// its expiry without decrypting, so treat expiry as informational.
    /// </summary>
    [JsonIgnore]
    public string StatusText => Loc.T(TokenExpired ? "status.tokenWillRefresh" : "status.ready");

    [JsonIgnore]
    public string PlanBadge => SubscriptionType.ToUpperInvariant() switch
    {
        "MAX" => "MAX",
        "PRO" => "PRO",
        "TEAM" => "TEAM",
        "ENTERPRISE" => "ENT",
        "" => "—",
        _ => SubscriptionType.ToUpperInvariant()
    };

    [JsonIgnore]
    public string Initial =>
        string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName.Trim()[..1].ToUpperInvariant();

    // ── cached real usage (from the /api/oauth/usage endpoint) ────────────────
    // Persisted so a non-active account can still show its last-known usage: only the active
    // account has a guaranteed-fresh token to query with. UsageFetchedAt dates the numbers.

    public double? UsageFiveHourPercent { get; set; }
    public DateTimeOffset? UsageFiveHourResetsAt { get; set; }
    public double? UsageSevenDayPercent { get; set; }
    public DateTimeOffset? UsageSevenDayResetsAt { get; set; }
    public DateTimeOffset? UsageFetchedAt { get; set; }

    /// <summary>Recent 5-hour utilization samples for the card's trend sparkline (oldest first).</summary>
    public List<UsageSample> UsageHistory { get; set; } = [];

    /// <summary>Appends a sample and trims the history to the most recent <paramref name="cap"/>.</summary>
    public void RecordUsageSample(double fivePercent, int cap = 72)
    {
        UsageHistory.Add(new UsageSample
        {
            T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Five = fivePercent,
        });
        if (UsageHistory.Count > cap)
            UsageHistory.RemoveRange(0, UsageHistory.Count - cap);
    }
}

/// <summary>One point of usage history: a timestamp and the 5-hour utilization then.</summary>
internal sealed class UsageSample
{
    public long T { get; set; }
    public double Five { get; set; }
}

/// <summary>
/// The secret half of a profile. Serialized, DPAPI-encrypted, and written to
/// &lt;id&gt;.bin — never touches disk in the clear.
/// </summary>
internal sealed class ProfileSecret
{
    /// <summary>Verbatim contents of .credentials.json, including any mcpOAuth entries.</summary>
    public string CredentialsJson { get; set; } = "";

    /// <summary>Raw JSON text of the oauthAccount object from .claude.json.</summary>
    public string OauthAccountRaw { get; set; } = "";

    /// <summary>Raw JSON text (a quoted string) of userID from .claude.json.</summary>
    public string UserIdRaw { get; set; } = "";
}
