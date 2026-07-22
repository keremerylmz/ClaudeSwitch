using System.Net.Http;
using System.Security.Cryptography;

namespace ClaudeSwitch.Core;

/// <summary>
/// Downloads a new build and swaps it in without the user leaving the app.
///
/// The swap relies on a Windows quirk worth stating plainly: a running executable cannot be
/// deleted or overwritten, but it CAN be renamed. So the live exe is moved aside to ".old", the
/// downloaded one takes its place, and the app relaunches itself from the same path. The stale
/// ".old" is swept on the next start, once nothing has it open.
///
/// Everything is staged and verified before that swap happens: a half-downloaded or truncated
/// file must never end up being the thing the user double-clicks tomorrow.
/// </summary>
internal static class Updater
{
    /// <summary>Downloads live here until they are proven good.</summary>
    private static string StagingDir => Path.Combine(ClaudePaths.AppDataDir, "update");

    /// <summary>Smallest plausible build; anything under this is an error page, not a binary.</summary>
    private const long MinimumSaneSize = 100 * 1024;

    internal enum Result { Ok, Failed, NotWritable }

    /// <summary>
    /// Fetches <paramref name="release"/> into staging, reporting 0–1 progress, and verifies it.
    /// Returns the staged path, or null when the download failed verification.
    /// </summary>
    public static async Task<string?> DownloadAsync(
        UpdateChecker.Release release, IProgress<double> progress, CancellationToken token)
    {
        Directory.CreateDirectory(StagingDir);
        var staged = Path.Combine(StagingDir, release.AssetName);

        try
        {
            using var http = UpdateChecker.NewClient();
            using var response = await http.GetAsync(
                release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? release.Size;

            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;

                while ((n = await source.ReadAsync(buffer, token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), token);
                    read += n;

                    // An unknown length still shows motion rather than a frozen bar at zero.
                    progress.Report(total > 0 ? Math.Min(1.0, (double)read / total) : 0.5);
                }
            }

            if (!Verify(staged, release))
            {
                TryDelete(staged);
                return null;
            }

            progress.Report(1.0);
            return staged;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                      or UnauthorizedAccessException)
        {
            TryDelete(staged);
            return null;
        }
    }

    /// <summary>
    /// Three cheap checks that together rule out the realistic failure: a truncated or
    /// intercepted download. The checksum is the real one; the others catch the case where the
    /// release predates checksum publishing.
    /// </summary>
    private static bool Verify(string path, UpdateChecker.Release release)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MinimumSaneSize) return false;

            // A truncated body is the common failure and the one that would otherwise ship a
            // broken exe; a mismatch against the announced size catches it immediately.
            if (release.Size > 0 && info.Length != release.Size) return false;

            // Every Windows executable starts "MZ".
            using (var stream = File.OpenRead(path))
            {
                if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') return false;
            }

            if (release.Sha256 is { Length: > 0 } expected)
                return string.Equals(Sha256(path), expected, StringComparison.OrdinalIgnoreCase);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Swaps <paramref name="staged"/> in for the running exe and relaunches. On success this
    /// does not return — the app is shutting down behind it.
    /// </summary>
    public static Result ApplyAndRestart(string staged)
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe)) return Result.Failed;

        var backup = exe + ".old";

        try
        {
            TryDelete(backup);

            // Rename rather than overwrite: Windows allows renaming a running image, so this is
            // the one move that can free the path while we are still executing from it.
            File.Move(exe, backup);

            try
            {
                File.Move(staged, exe);
            }
            catch (Exception)
            {
                File.Move(backup, exe);   // put the working build back before giving up
                throw;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Installed somewhere that needs elevation (Program Files, say). The caller falls
            // back to the releases page rather than silently doing nothing.
            return Result.NotWritable;
        }
        catch (IOException)
        {
            return Result.Failed;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
            });
            return Result.Ok;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return Result.Failed;
        }
    }

    /// <summary>
    /// Clears the previous build and any leftover staging. Runs at startup, when the old image
    /// is finally closed and therefore deletable.
    /// </summary>
    public static void SweepOldBuilds()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
            {
                var dir = Path.GetDirectoryName(exe);
                if (dir is not null)
                {
                    foreach (var stale in Directory.EnumerateFiles(dir, "*.old"))
                        TryDelete(stale);
                }
            }

            if (Directory.Exists(StagingDir))
                Directory.Delete(StagingDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers are harmless; they get another chance next launch.
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
