using System.Diagnostics;
using System.Text.Json;

namespace ClaudeSwitch.Core;

/// <summary>
/// Finds the Claude Code sessions running right now.
///
/// Claude Code drops one &lt;pid&gt;.json into ~/.claude/sessions while a session lives. Switching
/// accounts under a live session leaves that session holding the outgoing account's token, which
/// is where "VS Code asked me to Authorize again" comes from — so it is worth saying out loud
/// before the switch rather than letting the user discover it afterwards.
///
/// Read-only: nothing here writes to or deletes anything under ~/.claude.
/// </summary>
internal static class LiveSessions
{
    internal sealed record Session(int Pid, string Name, string Cwd, string Entrypoint)
    {
        /// <summary>"VS Code" or "terminal" — what the user should go look at.</summary>
        public string Surface => Entrypoint.Contains("vscode", StringComparison.OrdinalIgnoreCase)
            ? "VS Code"
            : Entrypoint.Contains("jetbrains", StringComparison.OrdinalIgnoreCase)
                ? "JetBrains"
                : "terminal";

        public string Label
        {
            get
            {
                var where = string.IsNullOrWhiteSpace(Cwd) ? Name : Path.GetFileName(Cwd.TrimEnd('\\', '/'));
                return string.IsNullOrWhiteSpace(where) ? Surface : $"{where} ({Surface})";
            }
        }
    }

    /// <summary>
    /// Every session whose process is still alive. Crashed sessions can leave their file behind,
    /// so the PID is always checked rather than trusted.
    /// </summary>
    public static IReadOnlyList<Session> Running()
    {
        var found = new List<Session>();

        try
        {
            if (!Directory.Exists(ClaudePaths.SessionsDir)) return found;

            foreach (var file in Directory.EnumerateFiles(ClaudePaths.SessionsDir, "*.json"))
            {
                if (Parse(file) is { } session && IsAlive(session.Pid))
                    found.Add(session);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A warning we couldn't compute must never block the switch.
        }

        return found;
    }

    private static Session? Parse(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;

            if (!root.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out var pid))
                return null;

            return new Session(pid, Str(root, "name"), Str(root, "cwd"), Str(root, "entrypoint"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        static string Str(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;   // no such process — a leftover file from a crash
        }
    }
}
