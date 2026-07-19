using System.Text;

namespace ClaudeSwitch.Core;

/// <summary>
/// Last line of defence. A tray app that vanishes on an unhandled exception leaves the user
/// with no idea what happened — and this one edits credential files, so "it just disappeared"
/// is an alarming failure mode. Log it, tell the user, and keep running when we safely can.
/// </summary>
internal static class CrashLog
{
    public static string LogPath => Path.Combine(ClaudePaths.AppDataDir, "errors.log");

    public static void Write(string context, Exception ex)
    {
        try
        {
            ClaudePaths.EnsureAppDirectories();

            var sb = new StringBuilder();
            sb.AppendLine(new string('-', 70));
            sb.AppendLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  [{context}]");
            sb.AppendLine($"{ex.GetType().FullName}: {ex.Message}");
            sb.AppendLine(ex.StackTrace);

            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            {
                sb.AppendLine($"  caused by {inner.GetType().FullName}: {inner.Message}");
                sb.AppendLine(inner.StackTrace);
            }

            File.AppendAllText(LogPath, sb.ToString());
        }
        catch (Exception)
        {
            // If even logging fails there is nothing sensible left to do.
        }
    }
}
