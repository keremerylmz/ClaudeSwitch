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

    /// <summary>Periodic refresh of every account's usage — including inactive ones.</summary>
    private System.Windows.Threading.DispatcherTimer? _usageTimer;
    private int _refreshAllInFlight;

    // Smart-limit state.
    private string? _lastNotifiedUuid;
    private int _lastNotifiedLevel;        // 0 · 80 · threshold — avoids repeat balloons
    private DateTimeOffset _lastAutoSwitch = DateTimeOffset.MinValue;

    private GlobalHotkey? _hotkey;

    /// <summary>Instant rate-limit notice from the optional Claude Code hook.</summary>
    private readonly LimitSignalWatcher _limitSignals = new();


    public MainWindow()
    {
        InitializeComponent();
        AccountList.ItemsSource = _items;
        RestoreWindowPlacement();

        Loaded += (_, _) =>
        {
            Refresh();
            // Populate every account's usage a few seconds after start, without waiting for the
            // first 10-minute tick.
            _ = DelayThenRefreshAllAsync();
        };

        _limitSignals.Received += signal =>
            Dispatcher.BeginInvoke(() => OnSessionRateLimited(signal));

        // A language change re-reads every string. Item-template text refreshes when the list
        // is rebuilt; the static chrome is re-set here.
        Loc.Changed += Relocalize;
        Closed += (_, _) => { Loc.Changed -= Relocalize; _hotkey?.Dispose(); _limitSignals.Dispose(); };

        // Tint the native title bar and register the global hotkey once the window has a handle.
        SourceInitialized += (_, _) =>
        {
            WindowChrome.Apply(this, ThemeManager.IsDark);
            ApplyHotkeySetting();
        };

        // Keep every account's usage current — the active one from its live token, the rest by
        // refreshing their stored tokens. Runs while the app lives in the tray, not just when the
        // window is open, so the numbers are fresh whenever you glance at them.
        _usageTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(10),
        };
        _usageTimer.Tick += (_, _) =>
        {
            _ = RefreshAllAccountsAsync();

            // Piggybacked rather than given its own timer: this app sits in the tray for days,
            // and an instance that only checked at startup would never see a release at all.
            _ = App.CheckForUpdatesAsync();
        };
        _usageTimer.Start();
    }

    /// <summary>
    /// Handles the two things a language change does NOT fix on its own.
    ///
    /// Everything written as {loc:Tr} now re-reads itself, so assigning those by hand here would
    /// be worse than redundant: a local value replaces a binding, which would quietly break the
    /// live updates for every later change.
    /// </summary>
    private void Relocalize()
    {
        // This label depends on whether a login is in flight, so no single key describes it.
        AddAccountButton.Content = _login is null ? Loc.T("footer.addAccount") : Loc.T("footer.cancel");

        // Card text built in C# (subtitles, "updated 3m ago", reset countdowns) is not bound,
        // so the items are rebuilt to pick the new language up.
        Refresh();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings();

    // ── settings layer ──────────────────────────────────────────────────────

    private SettingsPanel? _settingsPanel;

    /// <summary>True while the settings layer is on screen (or fading in).</summary>
    public bool SettingsOpen => SettingsLayer.Visibility == Visibility.Visible;

    private static readonly TimeSpan LayerFade = TimeSpan.FromMilliseconds(180);

    /// <summary>Fades the preferences layer in over the account list.</summary>
    public void ShowSettings()
    {
        if (SettingsOpen) return;

        // Built on first use, not at startup: most sessions never open it, and this is a tray
        // app whose whole point is staying small.
        if (_settingsPanel is null)
        {
            _settingsPanel = new SettingsPanel(App.Settings, () => Refresh());
            _settingsPanel.CloseRequested += HideSettings;
            SettingsLayer.Children.Add(_settingsPanel);
        }

        SettingsLayer.Visibility = Visibility.Visible;
        Animate(to: 1, shiftTo: 0, onDone: null);
    }

    public void HideSettings()
    {
        if (!SettingsOpen) return;
        Animate(to: 0, shiftTo: 10, onDone: () => SettingsLayer.Visibility = Visibility.Collapsed);
    }

    private void Animate(double to, double shiftTo, Action? onDone)
    {
        var ease = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
        };

        var fade = new System.Windows.Media.Animation.DoubleAnimation(to, LayerFade) { EasingFunction = ease };
        if (onDone is not null) fade.Completed += (_, _) => onDone();

        var slide = new System.Windows.Media.Animation.DoubleAnimation(shiftTo, LayerFade) { EasingFunction = ease };

        SettingsLayer.BeginAnimation(OpacityProperty, fade);
        SettingsShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
    }

    /// <summary>Escape backs out of settings — the layer has no title bar to close.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && SettingsOpen)
        {
            HideSettings();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>Registers or releases the global hotkey to match the current setting.</summary>
    public void ApplyHotkeySetting()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        if (App.Settings.GlobalHotkey)
            _hotkey = GlobalHotkey.Register(this, CycleToNextAccount);
    }

    /// <summary>Switches to the next switchable account after the active one — the hotkey action.</summary>
    private void CycleToNextAccount()
    {
        // The active account stays in the ring even when excluded, so cycling away from it works;
        // an excluded account is only ever skipped as a destination.
        var switchable = _items.Where(i => !i.NeedsReauth && (i.IsActive || !i.Profile.ExcludeFromAuto)).ToList();
        if (switchable.Count < 2) return;

        var activeIndex = switchable.FindIndex(i => i.IsActive);
        var next = switchable[(activeIndex + 1) % switchable.Count];
        if (!next.IsActive) SwitchTo(next.Profile, silent: true);
    }

    /// <summary>Closing the window parks the app in the tray; only the tray menu really exits.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowPlacement();

        if (!App.IsShuttingDown)
        {
            e.Cancel = true;
            Hide();
            MemoryTrim.Trim();
            return;
        }

        base.OnClosing(e);
    }

    // ── window placement ────────────────────────────────────────────────────

    /// <summary>
    /// Reopens the window where it was left. A saved rectangle is only honoured when it still
    /// falls on a connected monitor — otherwise unplugging a second screen would strand the
    /// window off-screen with no way to get it back.
    /// </summary>
    private void RestoreWindowPlacement()
    {
        var s = App.Settings;
        if (s.WindowWidth < MinWidth || s.WindowHeight < MinHeight) return;

        var rect = new System.Drawing.Rectangle(
            (int)s.WindowLeft, (int)s.WindowTop, (int)s.WindowWidth, (int)s.WindowHeight);

        if (!System.Windows.Forms.Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(rect)))
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = s.WindowLeft;
        Top = s.WindowTop;
        Width = s.WindowWidth;
        Height = s.WindowHeight;
    }

    private void SaveWindowPlacement()
    {
        // RestoreBounds carries the normal-state rectangle even while maximized or minimized.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (bounds.Width < MinWidth || bounds.Height < MinHeight) return;

        var s = App.Settings;
        if (Math.Abs(s.WindowLeft - bounds.Left) < 1 && Math.Abs(s.WindowTop - bounds.Top) < 1 &&
            Math.Abs(s.WindowWidth - bounds.Width) < 1 && Math.Abs(s.WindowHeight - bounds.Height) < 1)
            return;   // nothing moved; skip the write

        s.WindowLeft = bounds.Left;
        s.WindowTop = bounds.Top;
        s.WindowWidth = bounds.Width;
        s.WindowHeight = bounds.Height;
        s.Save();
    }

    // ── data ────────────────────────────────────────────────────────────────

    public void Refresh()
    {
        var profiles = SortProfiles(_store.LoadAll());
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

        // Feeds the optional Claude Code status line, which can't read ~/.claude.json itself.
        ClaudeCodeIntegration.WriteActiveLabel(
            _items.FirstOrDefault(i => i.IsActive)?.DisplayName ?? activeEmail ?? "");

        App.Tray?.Rebuild(_items.ToList());

        _ = RefreshUsageAsync(force: false);
    }

    /// <summary>
    /// Orders the list the way the user asked. "Recent" is the store's own order; the point of
    /// offering alternatives is that with several accounts the recent order reshuffles under you
    /// after every switch, which is exactly wrong for muscle memory.
    /// </summary>
    private static List<Profile> SortProfiles(IEnumerable<Profile> profiles) => App.Settings.AccountSort switch
    {
        "name" => profiles.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
        "free" => profiles.OrderBy(p => p.UsageFiveHourPercent ?? 101)
                          .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
        "plan" => profiles.OrderBy(p => PlanRank(p.SubscriptionType))
                          .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
        _ => profiles.ToList(),
    };

    private static int PlanRank(string subscription) => subscription.ToUpperInvariant() switch
    {
        "ENTERPRISE" => 0,
        "TEAM" => 1,
        "MAX" => 2,
        "PRO" => 3,
        _ => 4,
    };

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
            UpdateSmartState();
        }
        finally
        {
            Volatile.Write(ref _usageFetchInFlight, 0);
        }
    }

    /// <summary>
    /// Refreshes usage for EVERY account, not just the active one — the periodic job behind the
    /// 10-minute timer.
    ///
    /// The active account uses its live on-disk token (Claude Code keeps it fresh; we never
    /// rotate it ourselves). Each inactive account uses its stored token — refreshing it first
    /// via the OAuth refresh grant when the access token has aged out. Saving the rotated tokens
    /// back is also what keeps inactive profiles from going stale, so a switch to them always
    /// works.
    /// </summary>
    private async Task DelayThenRefreshAllAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        await RefreshAllAccountsAsync();
    }

    private async Task RefreshAllAccountsAsync()
    {
        if (Interlocked.Exchange(ref _refreshAllInFlight, 1) == 1) return;

        try
        {
            foreach (var item in _items.ToList())
            {
                try
                {
                    if (item.IsActive)
                    {
                        await RefreshActiveUsageAsync(item);
                    }
                    else
                    {
                        await RefreshInactiveAccountAsync(item);
                    }
                }
                catch (Exception ex)
                {
                    CrashLog.Write("RefreshAll", ex);
                }
            }

            UpdateSmartState();
        }
        finally
        {
            Volatile.Write(ref _refreshAllInFlight, 0);
        }
    }

    /// <summary>
    /// Derives everything that depends on fresh usage: the "most free" badge, the tray icon's
    /// colour, limit notifications, and — if enabled — auto-switching away from a maxed-out
    /// account. Called after any usage refresh.
    /// </summary>
    private void UpdateSmartState()
    {
        var usableInactive = _items
            .Where(i => !i.IsActive && !i.NeedsReauth && i.HasUsage)
            .ToList();

        // The account with the most 5-hour headroom right now.
        var mostFree = usableInactive.OrderBy(i => i.FiveHourValue).FirstOrDefault();
        foreach (var i in _items) i.IsMostFree = ReferenceEquals(i, mostFree) && usableInactive.Count > 0;

        var active = _items.FirstOrDefault(i => i.IsActive);
        App.Tray?.SetActiveUsage(active is { HasUsage: true } ? active.FiveHourValue : (double?)null,
                                 active?.DisplayName);

        if (active is not { HasUsage: true }) return;
        var pct = active.FiveHourValue;

        NotifyLimitIfNeeded(active, pct);

        if (App.Settings.AutoSwitch && pct >= App.Settings.AutoSwitchThreshold)
        {
            // The badge may point at an account the user has ring-fenced (a work or client seat).
            // Suggesting it is fine; moving into it unattended is not.
            var target = usableInactive
                .Where(i => !i.Profile.ExcludeFromAuto)
                .OrderBy(i => i.FiveHourValue)
                .FirstOrDefault();

            AutoSwitchIfWorthwhile(active, target);
        }
    }

    /// <summary>
    /// A live session just got rate-limited and the optional hook told us straight away. Say so
    /// now, and point at somewhere to go — waiting for the next poll would be up to ten minutes
    /// of the user staring at a blocked session.
    /// </summary>
    private void OnSessionRateLimited(LimitSignalWatcher.Signal signal)
    {
        if (!string.Equals(signal.ErrorType, "rate_limit", StringComparison.OrdinalIgnoreCase)) return;

        var target = _items
            .Where(i => !i.IsActive && !i.NeedsReauth && !i.Profile.ExcludeFromAuto && i.HasUsage)
            .OrderBy(i => i.FiveHourValue)
            .FirstOrDefault();

        App.Tray?.Notify(
            Loc.T("notify.rateLimitedTitle"),
            target is null
                ? Loc.T("notify.rateLimitedBody")
                : Loc.T("notify.rateLimitedSuggest", target.DisplayName, (int)target.FiveHourValue));

        // The numbers behind that suggestion are now the most interesting thing on screen.
        _ = RefreshAllAccountsAsync();
    }

    private void NotifyLimitIfNeeded(AccountItem active, double pct)
    {
        if (!App.Settings.LimitNotifications) return;

        var uuid = active.Profile.AccountUuid ?? "";
        if (uuid != _lastNotifiedUuid) { _lastNotifiedUuid = uuid; _lastNotifiedLevel = 0; }

        var threshold = App.Settings.AutoSwitchThreshold;
        var level = pct >= threshold ? threshold : pct >= 80 ? 80 : 0;
        if (level <= _lastNotifiedLevel) return;   // only notify on the way up
        _lastNotifiedLevel = level;

        if (level == 0) return;
        var msg = level >= threshold
            ? Loc.T("notify.atLimitBody", active.DisplayName)
            : Loc.T("notify.nearLimitBody", active.DisplayName, (int)pct);
        App.Tray?.Notify(Loc.T("notify.limitTitle"), msg);
    }

    private void AutoSwitchIfWorthwhile(AccountItem active, AccountItem? mostFree)
    {
        // Only switch to a clearly fresher account, and never more than once every few minutes,
        // so a pair of near-full accounts can't ping-pong.
        if (mostFree is null) return;
        if (DateTimeOffset.Now - _lastAutoSwitch < TimeSpan.FromMinutes(3)) return;
        if (mostFree.FiveHourValue > active.FiveHourValue - 15) return;

        _lastAutoSwitch = DateTimeOffset.Now;
        App.Tray?.Notify(Loc.T("notify.autoSwitchTitle"),
            Loc.T("notify.autoSwitchBody", mostFree.DisplayName));
        SwitchTo(mostFree.Profile);
    }

    /// <summary>Active account: fetch usage with the live on-disk token. Never refreshes it.</summary>
    private async Task RefreshActiveUsageAsync(AccountItem item)
    {
        if (!File.Exists(ClaudePaths.CredentialsFile)) return;
        var token = UsageApi.ExtractAccessToken(File.ReadAllText(ClaudePaths.CredentialsFile));
        if (token is null) return;

        var snapshot = await UsageApi.FetchAsync(token);
        if (snapshot is null) return;

        StoreUsage(item.Profile, snapshot);
        item.RefreshUsage();
    }

    /// <summary>
    /// Inactive account: renew the stored token if the access token has expired, then fetch its
    /// usage. Rotated tokens are saved back so the profile never goes stale.
    /// </summary>
    private async Task RefreshInactiveAccountAsync(AccountItem item)
    {
        ProfileSecret secret;
        try { secret = _store.LoadSecret(item.Profile.Id); }
        catch (Exception) { return; }

        var creds = secret.CredentialsJson;

        // Refresh only when the access token has actually expired — no point rotating a token
        // that still works, and it keeps refresh-endpoint traffic to a minimum.
        if (AccessTokenExpired(creds))
        {
            var (result, updated) = await TokenRefresher.RefreshAsync(creds);
            if (result == TokenRefresher.Result.Refreshed && updated is not null)
            {
                secret.CredentialsJson = updated;
                creds = updated;

                item.Profile.ExpiresAt = ReadExpiresAt(updated) ?? item.Profile.ExpiresAt;
                _store.Save(item.Profile, secret);   // persist rotated tokens
            }
            else if (result == TokenRefresher.Result.RefreshTokenDead)
            {
                item.NeedsReauth = true;
                return;
            }
            else
            {
                return;   // temporary failure; leave last-known numbers, try next cycle
            }
        }

        var token = UsageApi.ExtractAccessToken(creds);
        if (token is null) return;

        var snapshot = await UsageApi.FetchAsync(token);
        if (snapshot is null) return;

        StoreUsage(item.Profile, snapshot);
        item.RefreshUsage();
    }

    private void StoreUsage(Profile profile, UsageSnapshot snapshot)
    {
        profile.UsageFiveHourPercent = snapshot.FiveHourPercent;
        profile.UsageFiveHourResetsAt = snapshot.FiveHourResetsAt;
        profile.UsageSevenDayPercent = snapshot.SevenDayPercent;
        profile.UsageSevenDayResetsAt = snapshot.SevenDayResetsAt;
        profile.UsageFetchedAt = snapshot.FetchedAt;
        profile.RecordUsageSample(snapshot.FiveHourPercent);
        _store.Save(profile);
    }

    private static bool AccessTokenExpired(string credentialsJson)
    {
        var exp = ReadExpiresAt(credentialsJson);
        // Treat "unknown" as expired so we refresh rather than send a possibly-dead token. A
        // 60-second margin avoids racing an expiry that is seconds away.
        return exp is null || exp <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000;
    }

    private static long? ReadExpiresAt(string credentialsJson)
    {
        try
        {
            var oauth = JsonSurgeon.GetRawValue(credentialsJson, "claudeAiOauth");
            if (oauth is null) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(oauth);
            return doc.RootElement.TryGetProperty("expiresAt", out var v) && v.TryGetInt64(out var ms)
                ? ms : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
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

    // ── in-app update ───────────────────────────────────────────────────────

    private UpdateChecker.Release? _update;
    private string? _stagedUpdate;
    private bool _downloading;

    /// <summary>Surfaces a newer release in the footer. Called by the startup check.</summary>
    internal void OfferUpdate(UpdateChecker.Release release)
    {
        _update = release;
        UpdateTitle.Text = Loc.T("update.title", release.Tag.TrimStart('v', 'V'));
        UpdateBody.Text = Loc.T("update.body");
        UpdateButton.Content = Loc.T("update.action");
        UpdateButton.IsEnabled = true;
        UpdateProgressTrack.Visibility = Visibility.Collapsed;
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;

        // The one button walks three states: download → restart → (if anything went wrong)
        // hand off to the release page, which is always a way forward.
        if (_stagedUpdate is not null) { RestartIntoNewBuild(); return; }
        if (_update is null) { OpenReleasesPage(); return; }

        _downloading = true;
        UpdateButton.IsEnabled = false;
        UpdateBody.Text = Loc.T("update.downloading", 0);
        UpdateProgressTrack.Visibility = Visibility.Visible;
        SetUpdateProgress(0);

        var progress = new Progress<double>(fraction =>
        {
            SetUpdateProgress(fraction);
            UpdateBody.Text = Loc.T("update.downloading", (int)(fraction * 100));
        });

        try
        {
            var staged = await Updater.DownloadAsync(_update, progress, CancellationToken.None);

            if (staged is null)
            {
                // Verification failing is the interesting case: we would rather leave the user on
                // a working build and send them to the release page than install something we
                // could not vouch for.
                UpdateBody.Text = Loc.T("update.failed");
                UpdateProgressTrack.Visibility = Visibility.Collapsed;
                UpdateButton.Content = Loc.T("update.openPage");
                UpdateButton.IsEnabled = true;
                _update = null;
                return;
            }

            _stagedUpdate = staged;
            UpdateBody.Text = Loc.T("update.ready");
            UpdateButton.Content = Loc.T("update.restart");
            UpdateButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Update", ex);
            UpdateBody.Text = Loc.T("update.failed");
            UpdateButton.Content = Loc.T("update.openPage");
            UpdateButton.IsEnabled = true;
            _update = null;
        }
        finally
        {
            _downloading = false;
        }
    }

    private void SetUpdateProgress(double fraction)
    {
        var pct = Math.Clamp(fraction, 0, 1) * 100;
        UpdateDone.Width = new GridLength(pct, GridUnitType.Star);
        UpdateLeft.Width = new GridLength(100 - pct, GridUnitType.Star);
    }

    private void RestartIntoNewBuild()
    {
        if (_stagedUpdate is null) { OpenReleasesPage(); return; }

        UpdateButton.IsEnabled = false;
        UpdateBody.Text = Loc.T("update.restarting");

        switch (Updater.ApplyAndRestart(_stagedUpdate))
        {
            case Updater.Result.Ok:
                App.RequestShutdown();   // the replacement is already starting
                break;

            case Updater.Result.NotWritable:
                UpdateBody.Text = Loc.T("update.notWritable");
                UpdateButton.Content = Loc.T("update.openPage");
                UpdateButton.IsEnabled = true;
                _stagedUpdate = null;
                break;

            default:
                UpdateBody.Text = Loc.T("update.failed");
                UpdateButton.Content = Loc.T("update.openPage");
                UpdateButton.IsEnabled = true;
                _stagedUpdate = null;
                break;
        }
    }

    private static void OpenReleasesPage()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(UpdateChecker.ReleasesPage) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
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

    /// <summary>
    /// Applies a profile. Shared by the window buttons, the tray menu, the hotkey, and
    /// auto-switch. <paramref name="silent"/> suppresses the balloon for switches the user
    /// triggered without looking at the screen.
    /// </summary>
    internal void SwitchTo(Profile profile, bool silent = false)
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
            // so open sessions can just keep going. When we can name them, do — knowing exactly
            // which windows are about to change hands is more use than a generic reassurance.
            var note = LiveSessionNote() ?? Loc.T("switch.keepGoing");
            ShowStatus(Loc.T("switch.done", profile.DisplayName) + " " + note);

            if (!silent && App.Settings.SwitchNotifications)
                App.Tray?.Notify(Loc.T("switch.title"), $"{profile.DisplayName}\n{note}");

            // Only warn when the account is genuinely un-restorable — i.e. its REFRESH token has
            // expired. An expired ACCESS token is normal and self-heals: Claude Code (and our own
            // 10-minute background refresh) renew it from the refresh token. Checking the access
            // token here was the old false alarm that told users to re-add perfectly good accounts.
            if (!AccountSwitcher.CredentialsUsable(secret.CredentialsJson))
            {
                ShowStatus($"⚠ {profile.DisplayName}: the saved sign-in has expired. " +
                           "Use \"+ Add Account\" to sign into it once more.");
                App.Tray?.Notify("Re-sign-in needed",
                    $"{profile.DisplayName}'s saved sign-in expired. Add the account again.");
            }
        }
        catch (Exception ex)
        {
            ShowError("Switch failed", ex);
        }
    }

    /// <summary>
    /// Names the Claude Code sessions running right now, so the user knows what the switch
    /// just applied to. Null when there are none, or when the setting is off.
    /// </summary>
    private static string? LiveSessionNote()
    {
        if (!App.Settings.ShowLiveSessions) return null;

        // Several sessions in one project share a label, so collapse duplicates — "foo, foo"
        // reads like a bug, not information.
        var labels = LiveSessions.Running()
            .Select(s => s.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (labels.Count == 0) return null;

        var named = string.Join(", ", labels.Take(2));
        if (labels.Count > 2) named += $" +{labels.Count - 2}";

        return Loc.T("switch.liveSessions", named);
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

        menu.Items.Add(BuildColorMenu(item));

        var refreshTokens = new MenuItem
        {
            Header = Loc.T("menu.refreshTokens"),
            IsEnabled = item.IsActive,
            ToolTip = Loc.T("menu.refreshTokensTip"),
        };
        refreshTokens.Click += (_, _) => SaveCurrentAccount(announce: true);
        menu.Items.Add(refreshTokens);

        menu.Items.Add(new Separator());

        var exclude = new MenuItem
        {
            Header = Loc.T("menu.excludeFromAuto"),
            ToolTip = Loc.T("menu.excludeFromAutoTip"),
            IsCheckable = true,
            IsChecked = item.Profile.ExcludeFromAuto,
        };
        exclude.Click += (_, _) =>
        {
            item.Profile.ExcludeFromAuto = exclude.IsChecked;
            _store.Save(item.Profile);
            Refresh();
        };
        menu.Items.Add(exclude);

        menu.Items.Add(new Separator());

        var delete = new MenuItem { Header = Loc.T("menu.delete") };
        delete.Click += (_, _) => DeleteProfile(item);
        menu.Items.Add(delete);
    }

    /// <summary>
    /// Colour submenu for the account avatar. Personal, work, and client accounts often share
    /// an email prefix, so the generated initial collides and the list stops being scannable.
    /// </summary>
    private MenuItem BuildColorMenu(AccountItem item)
    {
        var root = new MenuItem { Header = Loc.T("menu.color") };

        var none = new MenuItem
        {
            Header = Loc.T("menu.colorDefault"),
            IsCheckable = true,
            IsChecked = string.IsNullOrEmpty(item.Profile.Color),
        };
        none.Click += (_, _) => SetColor(item, "");
        root.Items.Add(none);

        foreach (var accent in ThemeManager.Accents)
        {
            var entry = new MenuItem
            {
                Header = accent.Name,
                IsCheckable = true,
                IsChecked = item.Profile.Color == accent.Key,
            };
            var key = accent.Key;
            entry.Click += (_, _) => SetColor(item, key);
            root.Items.Add(entry);
        }

        return root;
    }

    private void SetColor(AccountItem item, string color)
    {
        item.Profile.Color = color;
        _store.Save(item.Profile);
        Refresh();
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

    private bool _needsReauth;

    /// <summary>Stored credentials are unusable — the account has to be added again.</summary>
    public bool NeedsReauth
    {
        get => _needsReauth;
        set
        {
            if (_needsReauth == value) return;
            _needsReauth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Subtitle));
        }
    }

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
        set
        {
            _isActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(AvatarBrush));
            OnPropertyChanged(nameof(AvatarTextBrush));
        }
    }

    /// <summary>
    /// The row button's label. A property rather than a template trigger because a trigger's
    /// Setter.Value cannot carry the live translation binding {loc:Tr} now produces — and one
    /// binding is less machinery than a trigger anyway.
    /// </summary>
    public string ActionLabel => Loc.T(IsActive ? "card.active" : "card.switch");

    // ── avatar ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The account's own colour when it has one, the accent when it's active, otherwise a
    /// neutral chip. A chosen colour outranks the active tint so the colour you assigned is
    /// always the thing you recognise the row by.
    /// </summary>
    public System.Windows.Media.Brush AvatarBrush
    {
        get
        {
            if (ThemeManager.Accents.FirstOrDefault(a => a.Key == Profile.Color) is { Key: not null } accent)
                return new System.Windows.Media.SolidColorBrush(ThemeManager.Parse(accent.Base));

            return Resource(IsActive ? "Accent" : "SurfaceHi");
        }
    }

    public System.Windows.Media.Brush AvatarTextBrush =>
        IsActive || !string.IsNullOrEmpty(Profile.Color)
            ? System.Windows.Media.Brushes.White
            : Resource("TextSecondary");

    public System.Windows.Visibility ExcludedVisibility =>
        Profile.ExcludeFromAuto ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    private static System.Windows.Media.Brush Resource(string key)
        => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[key];

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
            nameof(SparkPoints), nameof(SparkVisibility),
        })
            OnPropertyChanged(name);
    }

    public bool HasUsage => Profile.UsageFetchedAt is not null;

    /// <summary>5-hour utilization as a number; treated as full when unknown, for "most free" ranking.</summary>
    public double FiveHourValue => Profile.UsageFiveHourPercent ?? 100;

    // ── sparkline: 5-hour utilization over recent samples, scaled into a 74×16 box ──

    private const double SparkW = 74, SparkH = 16;

    public System.Windows.Visibility SparkVisibility =>
        Profile.UsageHistory.Count >= 2 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public System.Windows.Media.PointCollection SparkPoints
    {
        get
        {
            var points = new System.Windows.Media.PointCollection();
            var h = Profile.UsageHistory;
            if (h.Count < 2) return points;

            var n = h.Count;
            for (var i = 0; i < n; i++)
            {
                var x = SparkW * i / (n - 1);
                // 0% at the bottom, 100% at the top, with a 1px margin so the stroke isn't clipped.
                var y = SparkH - 1 - Math.Clamp(h[i].Five, 0, 100) / 100.0 * (SparkH - 2);
                points.Add(new System.Windows.Point(x, y));
            }
            return points;
        }
    }

    private bool _isMostFree;

    /// <summary>This inactive account currently has the most 5-hour headroom.</summary>
    public bool IsMostFree
    {
        get => _isMostFree;
        set { if (_isMostFree == value) return; _isMostFree = value; OnPropertyChanged(); OnPropertyChanged(nameof(MostFreeVisibility)); }
    }

    public System.Windows.Visibility MostFreeVisibility =>
        _isMostFree ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

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
