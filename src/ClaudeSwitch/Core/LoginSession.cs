using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeSwitch.Core;

/// <summary>
/// Drives one <c>claude auth login</c> run from inside the app.
///
/// The CLI prints the OAuth URL ("If the browser didn't open, visit: ...") and then waits on
/// "Paste code here if prompted >". Capturing that URL is what lets us re-open it in a private
/// window, which is the only way to reach a different account. Because stdout is redirected the
/// user can no longer see the terminal, so we also relay the code back through stdin.
///
/// Everything runs against a throwaway CLAUDE_CONFIG_DIR, so a failed or abandoned login
/// cannot disturb the account currently in use.
/// </summary>
internal sealed class LoginSession : IDisposable
{
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s""'<>]+", RegexOptions.Compiled);

    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly object _gate = new();

    private LoginSession(Process process, string configDir)
    {
        _process = process;
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

    /// <summary>Everything the CLI has printed so far — surfaced in the UI when things go wrong.</summary>
    public string Output
    {
        get { lock (_gate) return _output.ToString(); }
    }

    public static LoginSession Start(string configDir, string? email = null)
    {
        var exe = ClaudeCli.Resolve()
            ?? throw new FileNotFoundException(
                "Claude Code CLI not found. To install it: npm install -g @anthropic-ai/claude-code");

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

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Couldn't start the Claude Code CLI.");

        return new LoginSession(process, configDir);
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

    /// <summary>
    /// Waits for the CLI to print the authorize URL.
    /// Returns null if it never appears within <paramref name="timeout"/>.
    /// </summary>
    public async Task<string?> WaitForUrlAsync(TimeSpan timeout, CancellationToken token = default)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            if (TryExtractUrl(Output) is { } url) return url;

            // If it exited without ever printing a URL, waiting longer is pointless.
            if (HasExited)
                return TryExtractUrl(Output);

            await Task.Delay(150, token);
        }

        return TryExtractUrl(Output);
    }

    /// <summary>Picks the OAuth authorize URL out of the CLI's output.</summary>
    private static string? TryExtractUrl(string text)
    {
        foreach (Match m in UrlPattern.Matches(text))
        {
            var url = m.Value.TrimEnd('.', ',', ')');
            if (url.Contains("oauth", StringComparison.OrdinalIgnoreCase) &&
                url.Contains("authorize", StringComparison.OrdinalIgnoreCase))
                return url;
        }

        return null;
    }

    /// <summary>True once the CLI is asking for the code copied from the browser.</summary>
    public bool IsAwaitingCode =>
        Output.Contains("Paste code", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the CLI reported a completed login.</summary>
    public bool ReportedSuccess =>
        Output.Contains("Login successful", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sends the authorization code the user copied from the browser.</summary>
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
    }
}
