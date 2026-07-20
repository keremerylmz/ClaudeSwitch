using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClaudeSwitch.Core;

namespace ClaudeSwitch;

public partial class MainWindow : Window
{
    private readonly ProfileStore _store = new();
    private readonly AccountSwitcher _switcher = new();
    private readonly ObservableCollection<AccountItem> _items = [];

    /// <summary>Guards against overlapping usage fetches when refreshes come in bursts.</summary>
    private int _usageFetchInFlight;

    /// <summary>The login in progress, if any. Non-null means the button acts as "cancel".</summary>
    private LoginSession? _login;
    private CancellationTokenSource? _loginCancel;

    /// <summary>Throwaway CLAUDE_CONFIG_DIR the in-progress login writes into.</summary>
    private string? _loginConfigDir;

    /// <summary>Throwaway browser profile backing the session-free login window.</summary>
    private string? _browserProfileDir;





    public MainWindow()
    {
        InitializeComponent();
        AccountList.ItemsSource = _items;
        Loaded += (_, _) => Refresh();

        // A language change re-reads every string. Item-template text refreshes when the list
        // is rebuilt; the static chrome is re-set here.
        Loc.Changed += Relocalize;
        Closed += (_, _) => Loc.Changed -= Relocalize;

        // Tint the native title bar once the window has a handle.
        SourceInitialized += (_, _) => WindowChrome.Apply(this, ThemeManager.IsDark);
    }

    /// <summary>Re-applies the current language to the static chrome, then rebuilds the list.</summary>
    private void Relocalize()
    {
        EmptyTitle.Text = Loc.T("empty.title");
        EmptyBody.Text = Loc.T("empty.body");
        SaveCurrentButton.Content = Loc.T("footer.saveCurrent");
        CodeTitle.Text = Loc.T("code.title");
        CodeBody.Text = Loc.T("code.body");
        SubmitCodeButton.Content = Loc.T("code.submit");
        RefreshButton.ToolTip = Loc.T("tip.refresh");
        SettingsButton.ToolTip = Loc.T("tip.settings");
        AddAccountButton.Content = _login is null ? Loc.T("footer.addAccount") : Loc.T("footer.cancel");
        Refresh();   // rebuilds items so per-card text (Switch/Active/5-hour/7-day) re-localizes
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.OpenSettings();

    /// <summary>Closing the window parks the app in the tray; only the tray menu really exits.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsShuttingDown)
        {
            e.Cancel = true;
            Hide();
            MemoryTrim.Trim();
            return;
        }

        base.OnClosing(e);
    }

    // ── data ────────────────────────────────────────────────────────────────

    public void Refresh()
    {
        var profiles = _store.LoadAll();
        var activeUuid = CurrentAccountUuid();
        var activeEmail = AccountSwitcher.CurrentEmail();

        _items.Clear();
        foreach (var p in profiles)
        {
            _items.Add(new AccountItem(p)
            {
                IsActive = !string.IsNullOrEmpty(activeUuid)
                           && string.Equals(p.AccountUuid, activeUuid, StringComparison.OrdinalIgnoreCase),

                // Flagged up front so a dead profile is visible before it's switched into,
                // instead of surfacing as Claude Code's sign-in screen afterwards.
                NeedsReauth = !IsProfileUsable(p),

                Compact = App.Settings.Compact,
            });
        }

        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ActiveAccountText.Text = activeEmail is null
            ? Loc.T("app.notSignedIn")
            : Loc.T("app.activePrefix", activeEmail);

        // An account that is logged in but not yet saved is the main thing a new user needs to do.
        var currentIsSaved = _items.Any(i => i.IsActive);
        SaveCurrentButton.IsEnabled = activeEmail is not null && !currentIsSaved;

        App.Tray?.Rebuild(_items.ToList());

        _ = RefreshUsageAsync(force: false);
    }

    /// <summary>Minimum gap between usage fetches for one account — the endpoint 429s if hit hard.</summary>
    private static readonly TimeSpan UsageCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Fetches real usage from the /api/oauth/usage endpoint.
    ///
    /// Only the ACTIVE account is queried automatically: its token, read live from
    /// ~/.claude/.credentials.json, is guaranteed fresh. Other accounts show their last-known
    /// numbers (from when they were active), which is honest and avoids both stale-token 401s
    /// and hammering the rate limit. <paramref name="force"/> ignores the 5-minute cache for a
    /// manual refresh.
    /// </summary>
    private async Task RefreshUsageAsync(bool force)
    {
        var active = _items.FirstOrDefault(i => i.IsActive);
        if (active is null) return;

        // Re-render the "X ago" / reset countdowns even when we do not re-fetch.
        active.RefreshUsage();

        if (!force && active.Profile.UsageFetchedAt is { } last &&
            DateTimeOffset.Now - last < UsageCacheTtl)
            return;

        if (Interlocked.Exchange(ref _usageFetchInFlight, 1) == 1) return;

        try
        {
            // Live token for the active account — always current.
            if (!File.Exists(ClaudePaths.CredentialsFile)) return;
            var token = UsageApi.ExtractAccessToken(File.ReadAllText(ClaudePaths.CredentialsFile));
            if (token is null) return;

            var snapshot = await Task.Run(() => UsageApi.FetchAsync(token));
            if (snapshot is null)
            {
                if (force) ShowStatus("Couldn't fetch usage (rate limit or connection). Try again shortly.");
                return;
            }

            active.Profile.UsageFiveHourPercent = snapshot.FiveHourPercent;
            active.Profile.UsageFiveHourResetsAt = snapshot.FiveHourResetsAt;
            active.Profile.UsageSevenDayPercent = snapshot.SevenDayPercent;
            active.Profile.UsageSevenDayResetsAt = snapshot.SevenDayResetsAt;
            active.Profile.UsageFetchedAt = snapshot.FetchedAt;

            _store.Save(active.Profile);   // persist so a later switch shows last-known numbers
            active.RefreshUsage();
        }
        finally
        {
            Volatile.Write(ref _usageFetchInFlight, 0);
        }
    }

    /// <summary>Local-only health check on a profile's stored credentials. Never throws.</summary>
    private bool IsProfileUsable(Profile profile)
    {
        try
        {
            return AccountSwitcher.CredentialsUsable(_store.LoadSecret(profile.Id).CredentialsJson);
        }
        catch (Exception)
        {
            return false;   // unreadable secret is, for the user's purposes, a dead profile
        }
    }

    private static string? CurrentAccountUuid()
    {
        try
        {
            if (!File.Exists(ClaudePaths.ConfigFile)) return null;
            var raw = JsonSurgeon.GetRawValue(File.ReadAllText(ClaudePaths.ConfigFile), "oauthAccount");
            if (raw is null) return null;

            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("accountUuid", out var v) ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            return null;
        }
    }

    // ── switching ───────────────────────────────────────────────────────────

    /// <summary>
    /// Copies the live credentials back into whichever saved profile they belong to.
    ///
    /// This is not optional bookkeeping — it is what keeps switching working at all. OAuth
    /// refresh tokens ROTATE: while you use an account, Claude Code silently refreshes and
    /// writes a new refreshToken to disk. The copy in our profile then becomes stale, and
    /// restoring that stale token later makes the refresh fail — which is exactly what showed
    /// up as "VS Code signed me out and asked me to Authorize again" after switching back.
    ///
    /// Run immediately before every switch so the outgoing account is stored at its newest state.
    /// </summary>
    private void SyncActiveProfileTokens()
    {
        try
        {
            var captured = _switcher.CaptureCurrent();
            if (captured is null) return;

            var (fresh, secret) = captured.Value;
            if (string.IsNullOrEmpty(fresh.AccountUuid)) return;

            var stored = _store.LoadAll().FirstOrDefault(p =>
                string.Equals(p.AccountUuid, fresh.AccountUuid, StringComparison.OrdinalIgnoreCase));

            if (stored is null) return;   // active account isn't saved as a profile; nothing to update

            // Keep the user's own fields; refresh only what the credentials actually carry.
            stored.ExpiresAt = fresh.ExpiresAt;
            stored.SubscriptionType = fresh.SubscriptionType;
            _store.Save(stored, secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A failed sync must not block the switch the user asked for.
        }
    }

    /// <summary>Applies a profile. Shared by the window buttons and the tray menu.</summary>
    internal void SwitchTo(Profile profile)
    {
        try
        {
            // Save the outgoing account's newest tokens BEFORE overwriting the live files,
            // otherwise its stored refresh token goes stale and it can't be switched back to.
            SyncActiveProfileTokens();

            var secret = _store.LoadSecret(profile.Id);
            _switcher.Apply(profile, secret);

            profile.LastUsedAt = DateTimeOffset.UtcNow;
            _store.Save(profile);

            Refresh();

            // No restart nagging: Claude Code picks up the new credentials on the next message,
            // so open sessions can just keep going.
            const string note = "Just keep going in your open sessions.";
            ShowStatus($"Switched to {profile.DisplayName}. {note}");
            App.Tray?.Notify("Account switched", $"{profile.DisplayName}\n{note}");

            _ = VerifySwitchedTokenAsync(profile, secret);
        }
        catch (Exception ex)
        {
            ShowError("Switch failed", ex);
        }
    }

    /// <summary>
    /// Checks that the tokens we just restored are actually accepted, and says so plainly if
    /// they are not.
    ///
    /// Without this the failure is silent and baffling: the switch "succeeds", then Claude Code
    /// quietly drops you on its sign-in screen. A rejected token means the saved copy went stale
    /// (refresh tokens rotate) and the account has to be added again — worth stating outright
    /// rather than leaving the user to guess.
    /// </summary>
    private async Task VerifySwitchedTokenAsync(Profile profile, ProfileSecret secret)
    {
        var token = UsageApi.ExtractAccessToken(secret.CredentialsJson);
        if (token is null) return;

        var (_, status) = await UsageApi.FetchWithStatusAsync(token);
        if (status != UsageApi.FetchStatus.Unauthorized) return;   // fine, or merely offline

        ShowStatus($"⚠ {profile.DisplayName}: the saved sign-in is no longer valid. " +
                   "Use \"+ Add Account\" to sign into it once more — after that it will keep working.");

        App.Tray?.Notify("Re-sign-in needed",
            $"{profile.DisplayName}'s saved sign-in expired. Add the account again.");
    }

    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is AccountItem item) SwitchTo(item.Profile);
    }

    // ── saving / adding ─────────────────────────────────────────────────────

    private void SaveCurrentButton_Click(object sender, RoutedEventArgs e) => SaveCurrentAccount(announce: true);

    /// <summary>
    /// Snapshots whoever is logged in right now. Re-saving an already-known account refreshes
    /// its tokens in place instead of creating a duplicate entry.
    /// </summary>
    private Profile? SaveCurrentAccount(bool announce)
    {
        try
        {
            var captured = _switcher.CaptureCurrent();
            if (captured is null)
            {
                if (announce) ShowStatus("No signed-in session to save. Sign into Claude Code first.");
                return null;
            }

            var (profile, secret) = captured.Value;

            var existing = _store.LoadAll().FirstOrDefault(p =>
                !string.IsNullOrEmpty(p.AccountUuid) &&
                string.Equals(p.AccountUuid, profile.AccountUuid, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                // Keep the user's custom label and creation date; take everything else fresh.
                profile.Id = existing.Id;
                profile.Label = existing.Label;
                profile.CreatedAt = existing.CreatedAt;
            }

            profile.LastUsedAt = DateTimeOffset.UtcNow;
            _store.Save(profile, secret);

            Refresh();

            if (announce)
            {
                ShowStatus(existing is not null
                    ? $"{profile.DisplayName} updated."
                    : $"{profile.DisplayName} saved.");
            }

            return profile;
        }
        catch (Exception ex)
        {
            ShowError("Save failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Adds an account with no intermediate screens: the login runs in an isolated config
    /// directory, its URL is re-opened in a private window, and the stray tab Claude Code
    /// opens in the ordinary browser is closed before it can be clicked.
    /// </summary>
    private async void AddAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_login is not null) { CancelLogin(); return; }

        if (!ClaudeCli.IsInstalled)
        {
            MessageBox.Show(this,
                "Claude Code CLI not found.\n\nTo install it:\nnpm install -g @anthropic-ai/claude-code",
                "ClaudeSwitch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Snapshot the live account first so it stays switchable even if the user never
        // saved it by hand. Nothing about it is modified by the login below.
        SaveCurrentAccount(announce: false);

        _loginCancel = new CancellationTokenSource();
        var token = _loginCancel.Token;

        AddAccountButton.Content = Loc.T("footer.cancel");
        ShowStatus("Preparing login… A browser window will come to the front.");

        try
        {
            // Titles of every browser window as they are right now. Anything that changes
            // into a login page after this point is Claude Code's doing, not the user's.
            var windowsBefore = BrowserTabs.Snapshot();

            _loginConfigDir = ClaudePaths.CreateScratchConfigDir();
            _login = LoginSession.Start(_loginConfigDir);

            var url = await _login.WaitForUrlAsync(TimeSpan.FromSeconds(30), token);
            if (url is null)
            {
                ShowStatus("Couldn't get the login URL: " + Truncate(_login.Output, 200));
                CancelLogin();
                return;
            }

            var session = PrivateBrowser.Open(url);
            if (session is null)
            {
                PrivateBrowser.OpenDefault(url);
                ShowStatus("No recognized browser found; opened in your default browser instead.");
            }
            else
            {
                _browserProfileDir = session.Value.ProfileDir;
                ShowStatus($"Sign in using the {session.Value.Browser.Name} window…");

                // Maximise and raise the login window once it appears. Located via the
                // before-snapshot, not the launched process, which may already have exited.
                _ = PrivateBrowser.FocusOnceAsync(windowsBefore, token);
            }

            // Also get rid of the tab Claude Code opened in the ordinary browser, which shows
            // the old account.
            _ = BrowserTabs.CloseStrayLoginTabAsync(windowsBefore, TimeSpan.FromSeconds(12), token);

            await WaitForLoginAsync(token);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Hesap ekleme iptal edildi.");
        }
        catch (Exception ex)
        {
            CrashLog.Write("AddAccount", ex);
            ShowStatus("Hesap eklenemedi: " + ex.Message);
        }
        finally
        {
            CleanUpLogin();
        }
    }

    /// <summary>
    /// Waits for credentials to appear in the scratch directory. That file is the real
    /// completion signal — more reliable than reading the CLI's console text.
    /// </summary>
    /// <summary>
    /// The browser callback usually completes the login on its own within a few seconds. The
    /// CLI prints "Paste code here if prompted" every time regardless, so that text is NOT a
    /// signal that a paste is actually needed — treating it as one made the code box pop up and
    /// yank the window forward on every login. The paste box is therefore held back until the
    /// login has clearly stalled.
    /// </summary>
    private static readonly TimeSpan ShowCodeBoxAfter = TimeSpan.FromSeconds(25);

    private async Task WaitForLoginAsync(CancellationToken token)
    {
        var started = DateTime.UtcNow;
        var deadline = started + TimeSpan.FromMinutes(5);
        var showedCodeBox = false;

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(1000, token);

            if (_login is null || _loginConfigDir is null) return;

            if (_switcher.CaptureFromConfigDir(_loginConfigDir) is { } captured)
            {
                // Don't trust the files alone — prove the token works before saving it. A
                // credential set that looks complete but is rejected means the login is still
                // settling, and storing it would produce a profile that silently fails later.
                var accessToken = UsageApi.ExtractAccessToken(captured.Secret.CredentialsJson);
                if (accessToken is not null)
                {
                    var (_, status) = await UsageApi.FetchWithStatusAsync(accessToken, token);
                    if (status == UsageApi.FetchStatus.Unauthorized)
                    {
                        ShowStatus("Finishing sign-in…");
                        continue;   // keep polling; the CLI hasn't finished yet
                    }
                }

                SaveAddedAccount(captured.Profile, captured.Secret);
                return;
            }

            // Only surface the paste box once the login has clearly not auto-completed.
            if (!showedCodeBox && _login.IsAwaitingCode &&
                DateTime.UtcNow - started > ShowCodeBoxAfter)
            {
                showedCodeBox = true;
                CodePanel.Visibility = Visibility.Visible;
                ShowStatus("Login didn't complete automatically. If the browser gave you a code, " +
                           "paste it below.");

                // Now it is right to raise the app: the user must come back here to paste.
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Show();
                Activate();
                CodeBox.Focus();
            }

            if (_login.HasExited && !_login.ReportedSuccess)
            {
                ShowStatus("Login didn't complete: " + Truncate(_login.Output, 200));
                return;
            }
        }

        ShowStatus("Login timed out.");
    }

    private void SubmitCodeButton_Click(object sender, RoutedEventArgs e) => SubmitCode();

    private void CodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) SubmitCode();
    }

    private void SubmitCode()
    {
        var code = CodeBox.Text.Trim();
        if (code.Length == 0 || _login is null) return;

        _login.SubmitCode(code);
        CodeBox.Clear();
        CodePanel.Visibility = Visibility.Collapsed;
        ShowStatus("Code submitted, verifying…");
    }

    private void SaveAddedAccount(Profile profile, ProfileSecret secret)
    {
        // Re-adding a known account refreshes its tokens rather than duplicating the row.
        var existing = _store.LoadAll().FirstOrDefault(p =>
            !string.IsNullOrEmpty(p.AccountUuid) &&
            string.Equals(p.AccountUuid, profile.AccountUuid, StringComparison.OrdinalIgnoreCase));

        var wasAlreadyActive = existing is not null && string.Equals(
            existing.AccountUuid, CurrentAccountUuid(), StringComparison.OrdinalIgnoreCase);

        if (existing is not null)
        {
            profile.Id = existing.Id;
            profile.Label = existing.Label;
            profile.CreatedAt = existing.CreatedAt;
        }

        _store.Save(profile, secret);
        Refresh();

        if (wasAlreadyActive)
        {
            ShowStatus($"{profile.DisplayName} was already saved — its tokens were refreshed. " +
                       "For a different account, sign in as that account in the private window.");
        }
        else
        {
            ShowStatus($"{profile.DisplayName} added. Click \"Switch\" to use it.");
            App.Tray?.Notify("Account added", profile.DisplayName);
        }
    }

    private void CancelLogin() => _loginCancel?.Cancel();

    private void CleanUpLogin()
    {
        _loginCancel?.Dispose();
        _loginCancel = null;

        _login?.Dispose();
        _login = null;

        // The scratch directory holds a complete credential set — never leave it in %TEMP%.
        if (_loginConfigDir is not null)
        {
            try { Directory.Delete(_loginConfigDir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _loginConfigDir = null;
        }

        // Best effort: the browser usually still has the profile open at this point, so it is
        // also swept on the next startup.
        PrivateBrowser.CleanUpProfile(_browserProfileDir);
        _browserProfileDir = null;

        CodePanel.Visibility = Visibility.Collapsed;
        AddAccountButton.Content = Loc.T("footer.addAccount");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ── row menu ────────────────────────────────────────────────────────────

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not AccountItem item) return;

        var menu = new ContextMenu { PlacementTarget = button, IsOpen = true };

        var rename = new MenuItem { Header = Loc.T("menu.rename") };
        rename.Click += (_, _) => RenameProfile(item);
        menu.Items.Add(rename);

        var refreshTokens = new MenuItem
        {
            Header = Loc.T("menu.refreshTokens"),
            IsEnabled = item.IsActive,
            ToolTip = Loc.T("menu.refreshTokensTip"),
        };
        refreshTokens.Click += (_, _) => SaveCurrentAccount(announce: true);
        menu.Items.Add(refreshTokens);

        menu.Items.Add(new Separator());

        var delete = new MenuItem { Header = Loc.T("menu.delete") };
        delete.Click += (_, _) => DeleteProfile(item);
        menu.Items.Add(delete);
    }

    private void RenameProfile(AccountItem item)
    {
        var dialog = new RenameDialog(item.Profile.DisplayName,
            title: Loc.T("menu.rename"), label: Loc.T("dialog.rename.label")) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        item.Profile.Label = dialog.NewName;
        _store.Save(item.Profile);
        Refresh();
    }

    private void DeleteProfile(AccountItem item)
    {
        var warning = item.IsActive
            ? "\n\nThis account is currently active. Deleting the profile does not sign you out, " +
              "but you won't be able to switch back to it with one click — you'd have to sign in again."
            : "";

        var confirm = MessageBox.Show(this,
            $"Delete the \"{item.Profile.DisplayName}\" profile?{warning}",
            "Delete Profile", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        _store.Delete(item.Profile.Id);
        Refresh();
        ShowStatus($"{item.Profile.DisplayName} deleted.");
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        Refresh();
        _ = RefreshUsageAsync(force: true);   // manual refresh bypasses the 5-minute cache
    }

    // ── status ──────────────────────────────────────────────────────────────

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void ShowError(string title, Exception ex)
    {
        ShowStatus($"{title}: {ex.Message}");
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

/// <summary>View model wrapper: a profile plus whether it is the account Claude Code is using.</summary>
internal sealed class AccountItem : INotifyPropertyChanged
{
    private bool _isActive;

    public AccountItem(Profile profile) => Profile = profile;

    public Profile Profile { get; }

    public string DisplayName => Profile.DisplayName;
    public string Initial => Profile.Initial;
    public string PlanBadge => Profile.PlanBadge;

    /// <summary>Stored credentials are unusable — the account has to be added again.</summary>
    public bool NeedsReauth { get; init; }

    /// <summary>Compact mode collapses the usage panel for a denser list.</summary>
    public bool Compact { get; init; }

    public System.Windows.Visibility UsageVisibility =>
        Compact ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public string Subtitle
    {
        get
        {
            // The most important thing to say about this account, so it replaces the usual detail.
            if (NeedsReauth) return Loc.T("reauth.subtitle");

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Profile.Email) && Profile.Email != DisplayName)
                parts.Add(Profile.Email);

            // Personal accounts get an auto-generated "<email>'s Organization" that just
            // repeats the email — noise, so leave it out.
            var org = Profile.OrganizationName;
            if (!string.IsNullOrWhiteSpace(org) &&
                !org.StartsWith(Profile.Email, StringComparison.OrdinalIgnoreCase))
                parts.Add(org);

            if (parts.Count == 0) parts.Add(Profile.StatusText);
            return string.Join(" · ", parts);
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnPropertyChanged(); }
    }

    // ── real usage (from /api/oauth/usage) ───────────────────────────────────

    /// <summary>Re-reads the usage fields off the profile after a fetch updated them.</summary>
    public void RefreshUsage()
    {
        foreach (var name in new[]
        {
            nameof(FiveHourText), nameof(FiveHourReset), nameof(FiveHourStar),
            nameof(FiveHourRestStar), nameof(FiveHourBrush),
            nameof(SevenDayText), nameof(SevenDayReset), nameof(SevenDayStar),
            nameof(SevenDayRestStar), nameof(SevenDayBrush),
            nameof(UsageAsOf), nameof(UsageTooltip),
        })
            OnPropertyChanged(name);
    }

    private bool HasUsage => Profile.UsageFetchedAt is not null;

    public string FiveHourText => HasUsage ? $"{Profile.UsageFiveHourPercent:0}%" : "…";
    public string SevenDayText => HasUsage ? $"{Profile.UsageSevenDayPercent:0}%" : "…";

    public GridLength FiveHourStar => Star(Profile.UsageFiveHourPercent);
    public GridLength FiveHourRestStar => Star(100 - (Profile.UsageFiveHourPercent ?? 0));
    public GridLength SevenDayStar => Star(Profile.UsageSevenDayPercent);
    public GridLength SevenDayRestStar => Star(100 - (Profile.UsageSevenDayPercent ?? 0));

    private static GridLength Star(double? percent)
        => new(Math.Clamp(percent ?? 0, 0, 100), GridUnitType.Star);

    public System.Windows.Media.Brush FiveHourBrush => BarBrush(Profile.UsageFiveHourPercent);
    public System.Windows.Media.Brush SevenDayBrush => BarBrush(Profile.UsageSevenDayPercent);

    /// <summary>Green under 70%, amber to 90%, red above — the usual "getting close" cue.</summary>
    private static System.Windows.Media.Brush BarBrush(double? percent)
    {
        var p = percent ?? 0;
        var color = p switch
        {
            >= 90 => System.Windows.Media.Color.FromRgb(0xB4, 0x44, 0x3A),
            >= 70 => System.Windows.Media.Color.FromRgb(0xC9, 0x64, 0x42),
            _ => System.Windows.Media.Color.FromRgb(0x2F, 0x7A, 0x5B),
        };
        return new System.Windows.Media.SolidColorBrush(color);
    }

    public string FiveHourReset => ResetText(Profile.UsageFiveHourResetsAt);
    public string SevenDayReset => ResetText(Profile.UsageSevenDayResetsAt);

    private string ResetText(DateTimeOffset? resetsAt)
    {
        if (!HasUsage || resetsAt is not { } at) return "";

        var left = at - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return Loc.T("usage.resetting");

        var when = left.TotalHours >= 24
            ? $"{at.ToLocalTime():d MMM HH:mm}"          // days away: show the date
            : left.TotalHours >= 1
                ? Loc.T("usage.resetsInHM", (int)left.TotalHours, left.Minutes)
                : Loc.T("usage.resetsInM", (int)left.TotalMinutes);

        return Loc.T("usage.resetsPrefix", when);
    }

    public string UsageAsOf
    {
        get
        {
            if (Profile.UsageFetchedAt is not { } at)
                return IsActive ? Loc.T("usage.fetching") : Loc.T("usage.updatesOnSwitch");

            var ago = DateTimeOffset.Now - at;
            var when = ago.TotalMinutes < 1 ? Loc.T("usage.justNow")
                : ago.TotalMinutes < 60 ? Loc.T("usage.minAgo", (int)ago.TotalMinutes)
                : ago.TotalHours < 24 ? Loc.T("usage.hourAgo", (int)ago.TotalHours)
                : Loc.T("usage.dayAgo", (int)ago.TotalDays);

            return Loc.T("usage.updatedPrefix", when);
        }
    }

    public string UsageTooltip => Loc.T("usage.tooltip");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
