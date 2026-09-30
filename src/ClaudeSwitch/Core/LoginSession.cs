using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeSwitch.Core;

/// <summary>
/// Drives one <c>claude auth login</c> run from inside the app.
///
/// The CLI works with two sign-in URLs. The one it PRINTS ("If the browser didn't open,
/// visit: …") is the manual flow: after authorizing, the page shows a code that has to be pasted
/// back. The one it hands to a browser redirects to a listener the CLI runs on localhost, so the
/// login completes by itself. Claude Code opens that page by running whatever the BROWSER
/// variable names with the URL as its only argument (verified against 2.1.278). Pointing BROWSER
/// at this very exe therefore hands us the self-completing URL — and since the CLI then never
/// opens a browser on its own, the user gets exactly one sign-in page, opened the way they chose.
///
/// Everything runs against a throwaway CLAUDE_CONFIG_DIR, so a failed or abandoned login
/// cannot disturb the account currently in use.
/// </summary>
internal sealed class LoginSession : IDisposable
{
    /// <summary>Inherited by the CLI's browser handoff: the file to drop the URL into.</summary>
    private const string SinkVariable = "CLAUDESWITCH_AUTH_URL_SINK";
    private const string SinkFileName = ".claudeswitch-auth-url";
    private const string ScratchPrefix = "claudeswitch-login-";

    /// <summary>
    /// How long to wait for the handoff once the CLI has printed its URL. The CLI starts its
    /// "browser" straight after printing, so this only runs out on a Claude Code old enough to
    /// ignore BROWSER — the login then falls back to the printed, code-paste URL.
    /// </summary>
    private static readonly TimeSpan HandoffGrace = TimeSpan.FromSeconds(8);

    private static readonly Regex UrlPattern = new(@"https?://[^\s""'<>\x1b]+", RegexOptions.Compiled);
    private static readonly Regex AnsiPattern = new(
        @"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)", RegexOptions.Compiled);

    private readonly Process _process;
    private readonly string _sink;
    private readonly StringBuilder _output = new();
    private readonly object _gate = new();

    private LoginSession(Process process, string configDir, string sink)
    {
        _process = process;
        _sink = sink;
        ConfigDir = configDir;

        // Read as raw blocks rather than lines: the code prompt has no trailing newline,
        // so a line-based reader would sit on it forever.
        _ = PumpAsync(process.StandardOutput);
        _ = PumpAsync(process.StandardError);
    }

    /// <summary>The scratch directory the new account's credentials will land in.</summary>
    public string ConfigDir { get; }

    public bool HasExited
    {
        get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } }
    }

    /// <summary>Everything the CLI has printed so far.</summary>
    public string Output
    {
        get { lock (_gate) return _output.ToString(); }
    }

    /// <summary>True when the CLI reported a completed login.</summary>
    public bool ReportedSuccess =>
        Output.Contains("Login successful", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The CLI's own explanation, for when something goes wrong: its output minus the URLs and
    /// prompts, which are long and say nothing about the failure.
    /// </summary>
    public string Diagnostic
    {
        get
        {
            var lines = AnsiPattern.Replace(Output, "")
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 &&
                            !UrlPattern.IsMatch(l) &&
                            !l.StartsWith("Opening browser", StringComparison.OrdinalIgnoreCase) &&
                            !l.StartsWith("Paste code", StringComparison.OrdinalIgnoreCase))
                .TakeLast(3);

            var text = string.Join(" ", lines);
            return text.Length <= 240 ? text : text[..240] + "…";
        }
    }

    /// <summary>
    /// Starts <c>claude auth login</c> in a fresh scratch directory.
    /// <paramref name="email"/>, when given, is passed on as the sign-in page's login hint.
    /// </summary>
    public static LoginSession Start(string? email = null)
    {
        var exe = ClaudeCli.Resolve()
            ?? throw new FileNotFoundException(
                "Claude Code CLI not found. To install it: npm install -g @anthropic-ai/claude-code");

        var configDir = Path.Combine(Path.GetTempPath(), $"{ScratchPrefix}{Guid.NewGuid():n}");
        Directory.CreateDirectory(configDir);
        var sink = Path.Combine(configDir, SinkFileName);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        psi.ArgumentList.Add("auth");
        psi.ArgumentList.Add("login");
        psi.ArgumentList.Add("--claudeai");

        if (!string.IsNullOrWhiteSpace(email))
        {
            psi.ArgumentList.Add("--email");
            psi.ArgumentList.Add(email);
        }

        psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;

        // The browser handoff (see the class comment). Skipped when running under the dotnet
        // host, where ProcessPath is dotnet.exe rather than something that can catch a URL.
        if (Environment.ProcessPath is { } self &&
            !Path.GetFileName(self).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            psi.Environment["BROWSER"] = self;
            psi.Environment[SinkVariable] = sink;
        }

        try
        {
            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Couldn't start the Claude Code CLI.");
            return new LoginSession(process, configDir, sink);
        }
        catch
        {
            DeleteDirectory(configDir);
            throw;
        }
    }

    private async Task PumpAsync(StreamReader reader)
    {
        var buffer = new char[512];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                lock (_gate) _output.Append(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Process ended mid-read; whatever we captured is what we have.
        }
    }

    // ── sign-in URLs ────────────────────────────────────────────────────────

    /// <summary>The two ways into the sign-in page (see the class comment).</summary>
    internal readonly record struct SignInUrls(string? Automatic, string? Manual)
    {
        /// <summary>What to give the user: the self-completing URL whenever there is one.</summary>
        public string? Best => Automatic ?? Manual;

        /// <summary>Only the printed URL is known, so the page will end on a code to paste back.</summary>
        public bool NeedsCode => Automatic is null && Manual is not null;
    }

    /// <summary>
    /// Waits for the CLI to produce its sign-in URLs. Returns as soon as the self-completing one
    /// has been handed over; falls back to the printed one if the handoff never comes.
    /// </summary>
    public async Task<SignInUrls> WaitForUrlsAsync(TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        DateTime? printedAt = null;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var automatic = ReadHandoff();
            var manual = TryExtractUrl(Output);
            if (automatic is not null) return new(automatic, manual);

            if (manual is not null)
            {
                printedAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - printedAt > HandoffGrace) return new(null, manual);
            }

            if (HasExited || DateTime.UtcNow >= deadline)
                return new(ReadHandoff(), TryExtractUrl(Output));

            await Task.Delay(100, token);
        }
    }

    private string? ReadHandoff()
    {
        try
        {
            if (!File.Exists(_sink)) return null;
            var url = File.ReadAllText(_sink).Trim();
            return IsWebUrl(url) ? url : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;   // mid-rename; the next poll sees it whole
        }
    }

    /// <summary>Picks the OAuth authorize URL out of the CLI's printed output.</summary>
    private static string? TryExtractUrl(string text)
    {
        foreach (Match m in UrlPattern.Matches(AnsiPattern.Replace(text, "")))
        {
            var url = m.Value.TrimEnd('.', ',', ')');
            if (url.Contains("oauth", StringComparison.OrdinalIgnoreCase) &&
                url.Contains("authorize", StringComparison.OrdinalIgnoreCase))
                return url;
        }

        return null;
    }

    private static bool IsWebUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    // ── the other end of the handoff ────────────────────────────────────────

    /// <summary>
    /// Runs before anything else at startup. When this process is the "browser" Claude Code
    /// launched during one of our logins, it leaves the URL where the waiting session looks and
    /// returns true — the caller must exit at once, without a window, tray icon, or mutex.
    /// </summary>
    public static bool TryCatchHandoff(IEnumerable<string> args)
    {
        var sink = Environment.GetEnvironmentVariable(SinkVariable);
        if (string.IsNullOrWhiteSpace(sink)) return false;

        try
        {
            var url = args.FirstOrDefault(IsWebUrl);
            if (url is not null && IsOurSink(sink))
            {
                // Written aside and renamed into place, so the reader sees all of it or nothing.
                var temp = sink + ".tmp";
                File.WriteAllText(temp, url);
                File.Move(temp, sink, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException)
        {
            // The session falls back to the printed URL; nothing else to do from here.
        }

        return true;
    }

    /// <summary>Only ever write where one of our logins asked — never an arbitrary path.</summary>
    private static bool IsOurSink(string sink)
    {
        var full = Path.GetFullPath(sink);
        var dir = Path.GetDirectoryName(full);

        return Path.GetFileName(full) == SinkFileName &&
               dir is not null &&
               Path.GetFileName(dir).StartsWith(ScratchPrefix, StringComparison.OrdinalIgnoreCase) &&
               full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase);
    }

    // ── manual fallback ─────────────────────────────────────────────────────

    /// <summary>Sends the code the sign-in page showed (manual flow only).</summary>
    public void SubmitCode(string code)
    {
        try
        {
            _process.StandardInput.WriteLine(code.Trim());
            _process.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process is gone; the caller detects this via HasExited.
        }
    }

    // ── cleanup ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        _process.Dispose();

        // The scratch directory holds a complete credential set — never leave it in %TEMP%.
        DeleteDirectory(ConfigDir);
    }

    /// <summary>
    /// Clears login scratch directories a crash or a killed process left behind — they can hold a
    /// complete credential set — along with the throwaway browser profiles older versions created.
    /// Runs at startup, when no login of ours can be in progress.
    /// </summary>
    public static void SweepLeftovers()
    {
        foreach (var pattern in new[] { $"{ScratchPrefix}*", "claudeswitch-browser-*" })
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), pattern))
                    DeleteDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void DeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still held open for a moment; the next startup sweep gets it.
        }
    }
}
