using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeSwitch.Core;

/// <summary>
/// Persists profiles under %APPDATA%\ClaudeSwitch\profiles.
///
/// Metadata (&lt;id&gt;.json) is plain text on purpose — it holds nothing sensitive and users
/// should be able to audit it. Tokens (&lt;id&gt;.bin) go through DPAPI with CurrentUser scope,
/// so another account on the same machine cannot read them even with file access.
/// </summary>
internal sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// Extra entropy mixed into DPAPI. Not a secret (it ships in the binary) — it just scopes
    /// the ciphertext to this app so an unrelated process cannot unprotect it by accident.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClaudeSwitch.v1.profile");

    public ProfileStore() => ClaudePaths.EnsureAppDirectories();

    private static string MetaPath(string id) => Path.Combine(ClaudePaths.ProfilesDir, $"{id}.json");
    private static string SecretPath(string id) => Path.Combine(ClaudePaths.ProfilesDir, $"{id}.bin");

    /// <summary>Loads all profiles, newest-used first. Corrupt entries are skipped, not fatal.</summary>
    public List<Profile> LoadAll()
    {
        var result = new List<Profile>();
        if (!Directory.Exists(ClaudePaths.ProfilesDir)) return result;

        foreach (var file in Directory.EnumerateFiles(ClaudePaths.ProfilesDir, "*.json"))
        {
            try
            {
                var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(file));
                // A profile without its secret half cannot be applied — treat it as absent.
                if (p is not null && File.Exists(SecretPath(p.Id))) result.Add(p);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Skip unreadable profile; the rest of the list stays usable.
            }
        }

        return result
            .OrderByDescending(p => p.LastUsedAt ?? DateTimeOffset.MinValue)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Save(Profile profile, ProfileSecret? secret = null)
    {
        ClaudePaths.EnsureAppDirectories();
        AtomicFile.WriteAllText(MetaPath(profile.Id), JsonSerializer.Serialize(profile, JsonOpts));

        if (secret is not null)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(secret);
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            CryptographicOperations.ZeroMemory(plain);
            AtomicFile.WriteAllBytes(SecretPath(profile.Id), cipher);
        }
    }

    public ProfileSecret LoadSecret(string id)
    {
        var path = SecretPath(id);
        if (!File.Exists(path))
            throw new FileNotFoundException("This profile's credentials are missing. Delete the profile and add it again.", path);

        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            // Typically: profile copied from another machine or another Windows user.
            throw new InvalidOperationException(
                "Couldn't decrypt the credentials. Profiles can only be opened by the Windows user that created them.", ex);
        }

        try
        {
            return JsonSerializer.Deserialize<ProfileSecret>(plain)
                   ?? throw new InvalidDataException("Profile content is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Delete(string id)
    {
        foreach (var path in new[] { MetaPath(id), SecretPath(id) })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { /* file locked; leaving it is harmless */ }
        }
    }
}

/// <summary>
/// Write-to-temp-then-replace. A crash mid-write leaves the previous file intact rather than
/// a half-written one — which for .claude.json would mean a broken Claude Code install.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents) =>
        Write(path, tmp => File.WriteAllText(tmp, contents, new UTF8Encoding(false)));

    public static void WriteAllBytes(string path, byte[] contents) =>
        Write(path, tmp => File.WriteAllBytes(tmp, contents));

    private static void Write(string path, Action<string> writeTemp)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():n}.tmp");

        try
        {
            writeTemp(tmp);

            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, path);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch (IOException) { }
            }
        }
    }
}
