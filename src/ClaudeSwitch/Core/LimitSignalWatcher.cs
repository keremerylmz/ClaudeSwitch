using System.Text.Json;

namespace ClaudeSwitch.Core;

/// <summary>
/// Picks up the payloads dropped by the StopFailure hook (see <see cref="ClaudeCodeIntegration"/>)
/// and reports them as they arrive.
///
/// This is the whole point of the hook: a session that gets rate-limited says so immediately,
/// instead of the tray finding out on its next ten-minute poll.
/// </summary>
internal sealed class LimitSignalWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;

    internal readonly record struct Signal(string ErrorType, string Cwd);

    /// <summary>Raised on a background thread — marshal before touching UI.</summary>
    public event Action<Signal>? Received;

    public LimitSignalWatcher()
    {
        try
        {
            Directory.CreateDirectory(ClaudeCodeIntegration.SignalsDir);

            _watcher = new FileSystemWatcher(ClaudeCodeIntegration.SignalsDir, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, e) => Handle(e.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _watcher = null;   // no watcher just means no instant notifications
        }
    }

    private void Handle(string path)
    {
        try
        {
            var json = ReadWhenReady(path);
            if (json is null) return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Received?.Invoke(new Signal(Str(root, "error_type"), Str(root, "cwd")));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            // One-shot: the signal is consumed, and a stale file must not fire again on restart.
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        static string Str(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    /// <summary>The Created event can beat the writer to the punch, so give the file a moment.</summary>
    private static string? ReadWhenReady(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            catch (IOException)
            {
            }
            Thread.Sleep(120);
        }
        return null;
    }

    public void Dispose() => _watcher?.Dispose();
}
