using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace ClaudeSwitch.Core;

/// <summary>
/// The actual switch: snapshots the logged-in account into a profile, and restores a
/// profile back over Claude Code's live files.
/// </summary>
internal sealed class AccountSwitcher
{
    /// <summary>
    /// Caches in .claude.json that describe the *current* account's entitlements — plan limits,
    /// model access, credit grants. Carrying them across a switch makes Claude Code show the
    /// wrong plan until they expire. We delete rather than swap them: Claude Code refetches on
    /// next launch, and a missing cache is always safe whereas a stale one is not.
    /// </summary>
    private static readonly string[] AccountScopedCaches =
    [
        "modelAccessCache",
        "clientDataCache",
        "clientDataCacheSlots",
        "additionalModelOptionsCache",
        "additionalModelCostsCache",
        "orgModelDefaultCache",
        "overageCreditGrantCache",
        "passesEligibilityCache",
        "cachedExtraUsageDisabledReason",
        "autoCompactWindowsCache",
    ];

    /// <summary>True when there is a logged-in account we can snapshot.</summary>
    public static bool HasLiveAccount()
        => File.Exists(ClaudePaths.CredentialsFile) && File.Exists(ClaudePaths.ConfigFile);

    /// <summary>
    /// Reads the currently logged-in account out of Claude Code's files.
    /// Returns null when nobody is logged in.
    /// </summary>
    public (Profile Profile, ProfileSecret Secret)? CaptureCurrent()
        => CaptureFrom(ClaudePaths.CredentialsFile, ClaudePaths.ConfigFile);

    /// <summary>
    /// Reads a finished account out of an isolated login directory (see
    /// <see cref="ClaudePaths.CreateScratchConfigDir"/>). Returns null until the login
    /// completes and both files exist with a usable oauthAccount.
    /// </summary>
    public (Profile Profile, ProfileSecret Secret)? CaptureFromConfigDir(string configDir)
    {
        var (credentials, config) = ClaudePaths.FilesIn(configDir);
        return CaptureFrom(credentials, config);
    }

    /// <summary>
    /// True when a credentials file holds a usable, fully-written token set.
    ///
    /// This guard exists because of a real failure: during login, Claude Code writes
    /// oauthAccount into .claude.json before .credentials.json has finished being written.
    /// Capturing on the oauthAccount signal alone stored a half-written credential set — one
    /// observed profile ended up with no expiresAt at all and a token the server rejected, so
    /// switching to it dropped the user on the sign-in screen. Requiring every field to be
    /// present means we simply keep waiting until the login has really finished.
    /// </summary>
    /// <summary>
    /// Whether a stored credential set can still be restored: fully written, and with a refresh
    /// token that has not expired. Purely local — no network call — so it is cheap enough to run
    /// for every profile on each refresh, and lets the UI flag a dead profile BEFORE the user
    /// switches into it.
    /// </summary>
    public static bool CredentialsUsable(string credentialsJson)
    {
        if (!LooksComplete(credentialsJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(credentialsJson);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return false;

            // An expired ACCESS token is fine — Claude Code refreshes it. An expired REFRESH
            // token is not: nothing can revive it and the account must be added again.
            if (oauth.TryGetProperty("refreshTokenExpiresAt", out var r) && r.TryGetInt64(out var ms))
                return ms <= 0 || ms > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            return true;   // field absent: assume usable rather than nag wrongly
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksComplete(string credentialsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(credentialsJson);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return false;

            var hasAccess = oauth.TryGetProperty("accessToken", out var a) &&
                            !string.IsNullOrWhiteSpace(a.GetString());
            var hasRefresh = oauth.TryGetProperty("refreshToken", out var r) &&
                             !string.IsNullOrWhiteSpace(r.GetString());
            var hasExpiry = oauth.TryGetProperty("expiresAt", out var e) &&
                            e.TryGetInt64(out var ms) && ms > 0;

            return hasAccess && hasRefresh && hasExpiry;
        }
        catch (JsonException)
        {
            return false;   // still mid-write
        }
    }

    private (Profile Profile, ProfileSecret Secret)? CaptureFrom(string credentialsFile, string configFile)
    {
        if (!File.Exists(credentialsFile) || !File.Exists(configFile)) return null;

        string credentialsJson, configJson;
        try
        {
            credentialsJson = File.ReadAllText(credentialsFile);
            configJson = File.ReadAllText(configFile);
        }
        catch (IOException)
        {
            return null;   // being written right now; try again on the next poll
        }

        if (!LooksComplete(credentialsJson)) return null;

        var oauthAccountRaw = JsonSurgeon.GetRawValue(configJson, "oauthAccount");
        if (string.IsNullOrWhiteSpace(oauthAccountRaw)) return null;

        var userIdRaw = JsonSurgeon.GetRawValue(configJson, "userID") ?? "\"\"";

        var profile = new Profile();
        ReadAccountMetadata(oauthAccountRaw, profile);
        ReadCredentialMetadata(credentialsJson, profile);
        profile.Label = profile.Email;

        var secret = new ProfileSecret
        {
            CredentialsJson = credentialsJson,
            OauthAccountRaw = oauthAccountRaw,
            UserIdRaw = userIdRaw,
        };

        return (profile, secret);
    }

    /// <summary>
    /// Makes <paramref name="profile"/> the active account. Both live files are backed up
    /// before anything is written, and .claude.json is patched member-by-member so the
    /// user's project history and settings survive untouched.
    /// </summary>
    public void Apply(Profile profile, ProfileSecret secret)
    {
        if (!File.Exists(ClaudePaths.ConfigFile))
            throw new InvalidOperationException(
                "~/.claude.json not found. Run Claude Code once and sign in first.");

        BackupLiveFiles();

        var configJson = File.ReadAllText(ClaudePaths.ConfigFile);

        configJson = JsonSurgeon.SetRawValue(configJson, "oauthAccount", secret.OauthAccountRaw);
        if (!string.IsNullOrWhiteSpace(secret.UserIdRaw))
            configJson = JsonSurgeon.SetRawValue(configJson, "userID", secret.UserIdRaw);

        foreach (var cache in AccountScopedCaches)
            configJson = JsonSurgeon.RemoveMember(configJson, cache);

        // Credentials first: if the config write fails we would rather have a mismatched
        // config than a valid config pointing at tokens that were never written.
        AtomicFile.WriteAllText(ClaudePaths.CredentialsFile, secret.CredentialsJson);
        AtomicFile.WriteAllText(ClaudePaths.ConfigFile, configJson);

        HardenCredentialsFilePermissions();
    }

    /// <summary>Email of the account Claude Code is currently using, or null.</summary>
    public static string? CurrentEmail()
    {
        try
        {
            if (!File.Exists(ClaudePaths.ConfigFile)) return null;
            var raw = JsonSurgeon.GetRawValue(File.ReadAllText(ClaudePaths.ConfigFile), "oauthAccount");
            if (raw is null) return null;

            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("emailAddress", out var e) ? e.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Keeps the last 20 snapshots of the live files under %APPDATA%\ClaudeSwitch\backups.</summary>
    private static void BackupLiveFiles()
    {
        ClaudePaths.EnsureAppDirectories();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(ClaudePaths.BackupsDir, stamp);
        Directory.CreateDirectory(dir);

        foreach (var src in new[] { ClaudePaths.ConfigFile, ClaudePaths.CredentialsFile })
        {
            if (File.Exists(src))
                File.Copy(src, Path.Combine(dir, Path.GetFileName(src)), overwrite: true);
        }

        PruneBackups(keep: 20);
    }

    private static void PruneBackups(int keep)
    {
        try
        {
            var stale = new DirectoryInfo(ClaudePaths.BackupsDir)
                .GetDirectories()
                .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                .Skip(keep);

            foreach (var d in stale) d.Delete(recursive: true);
        }
        catch (IOException) { /* pruning is best-effort */ }
    }

    /// <summary>
    /// Claude Code writes .credentials.json with default inherited ACLs. Since we rewrite the
    /// file anyway, restrict it to the current user so other local accounts cannot read the
    /// tokens off disk.
    /// </summary>
    private static void HardenCredentialsFilePermissions()
    {
        try
        {
            var user = WindowsIdentity.GetCurrent().User;
            if (user is null) return;

            var info = new FileInfo(ClaudePaths.CredentialsFile);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user, FileSystemRights.FullControl, AccessControlType.Allow));

            info.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Non-fatal: the switch already succeeded, this is defence in depth.
        }
    }

    private static void ReadAccountMetadata(string oauthAccountRaw, Profile profile)
    {
        try
        {
            using var doc = JsonDocument.Parse(oauthAccountRaw);
            var root = doc.RootElement;

            profile.Email = Str(root, "emailAddress");
            profile.AccountUuid = Str(root, "accountUuid");
            profile.OrganizationName = Str(root, "organizationName");
            profile.SeatTier = Str(root, "seatTier");
        }
        catch (JsonException)
        {
            // Metadata is cosmetic; the profile is still usable without it.
        }

        static string Str(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
    }

    private static void ReadCredentialMetadata(string credentialsJson, Profile profile)
    {
        try
        {
            using var doc = JsonDocument.Parse(credentialsJson);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return;

            if (oauth.TryGetProperty("subscriptionType", out var sub) && sub.ValueKind == JsonValueKind.String)
                profile.SubscriptionType = sub.GetString() ?? "";

            if (oauth.TryGetProperty("expiresAt", out var exp) && exp.TryGetInt64(out var ms))
                profile.ExpiresAt = ms;
        }
        catch (JsonException) { }
    }
}
