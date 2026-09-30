using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeSwitch.Controls;
using ClaudeSwitch.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace ClaudeSwitch;

public partial class MainWindow : Window
{
    private readonly ProfileStore _store = new();
    private readonly AccountSwitcher _switcher = new();
    private readonly ObservableCollection<AccountItem> _items = [];
    private readonly ListAnimator _listAnimator;

    /// <summary>Guards against overlapping usage fetches when refreshes come in bursts.</summary>
    private int _usageFetchInFlight;

    /// <summary>Periodic refresh of every account's usage — including inactive ones.</summary>
    private DispatcherTimer? _usageTimer;
    private int _refreshAllInFlight;

    // Smart-limit state.
    private string? _lastNotifiedUuid;
    private int _lastNotifiedLevel;        // 0 · 80 · threshold — avoids repeat balloons
    private DateTimeOffset _lastAutoSwitch = DateTimeOffset.MinValue;

    private GlobalHotkey? _hotkey;

    /// <summary>Instant rate-limit notice from the optional Claude Code hook.</summary>
    private readonly LimitSignalWatcher _limitSignals = new();

    /// <summary>The add-account sheet, built the first time someone adds an account.</summary>
    private AddAccountSheet? _sheet;

    private bool _shownOnce;

    public MainWindow()
    {
        InitializeComponent();
        AccountList.ItemsSource = _items;
        _listAnimator = new ListAnimator(AccountList, item => ((AccountItem)item).Profile.Id);
        RestoreWindowPlacement();

        Loaded += (_, _) =>
        {
            Refresh();
            // Populate every account's usage a few seconds after start, without waiting for the
            // first 10-minute tick.
            _ = DelayThenRefreshAllAsync();
        };

        // Coming back from the tray or mini mode, the page settles into place instead of
        // popping. The very first show is left to the list's own staggered entrance.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true) return;
            if (_shownOnce) Motion.Rise(MainContent, 8, Motion.Medium);
            _shownOnce = true;
        };

        _limitSignals.Received += signal =>
            Dispatcher.BeginInvoke(() => OnSessionRateLimited(signal));

        // A language change re-reads every string. Everything written as {loc:Tr} updates by
        // itself; card text built in C# (subtitles, "updated 3m ago", reset countdowns) is not
        // bound, so the items are rebuilt to pick the new language up.
        Loc.Changed += Refresh;
        Closed += (_, _) => { Loc.Changed -= Refresh; _hotkey?.Dispose(); _limitSignals.Dispose(); };

        // Tint the native title bar and register the global hotkey once the window has a handle.
        SourceInitialized += (_, _) =>
        {
            WindowChrome.Apply(this, ThemeManager.IsDark);
            ApplyBackdrop();
            ApplyHotkeySetting();
        };

        // Keep every account's usage current — the active one from its live token, the rest by
        // refreshing their stored tokens. Runs while the app lives in the tray, not just when the
        // window is open, so the numbers are fresh whenever you glance at them.
        _usageTimer = new DispatcherTimer
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
    /// Puts the window on Mica when the setting is on and the OS supports it. The window's own
    /// background has to go transparent for the material to show — so when Mica is NOT in play,
    /// the themed opaque background is restored, otherwise the content would float on nothing.
    /// </summary>
    public void ApplyBackdrop()
    {
        var mica = App.Settings.Translucent && WindowChrome.SupportsMica;

        if (mica)
        {
            Background = Brushes.Transparent;
        }
        else
        {
            // SetResourceReference, not FindResource: a plain assignment captures the current Bg
            // brush as a static value and severs the DynamicResource binding, so the window would
            // keep the theme it had when this ran — which is exactly why the background stayed
            // dark after switching to light.
            SetResourceReference(BackgroundProperty, "Bg");
        }

        WindowChrome.ApplyBackdrop(this, mica);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void MiniButton_Click(object sender, RoutedEventArgs e) => App.EnterMiniMode();

    private void SearchButton_Click(object sender, RoutedEventArgs e) => ShowPalette();

    // ── settings layer ──────────────────────────────────────────────────────

    private SettingsPanel? _settingsPanel;
    private bool _settingsShown;

    /// <summary>True while the settings layer is on screen or on its way in.</summary>
    public bool SettingsOpen => _settingsShown;

    /// <summary>
    /// Slides the preferences in over the account list — a push, the same "one level deeper"
    /// gesture Windows uses everywhere: the settings arrive from the right while the page beneath
    /// drifts a little the other way.
    /// </summary>
    public void ShowSettings()
    {
        if (_settingsShown) return;
        _settingsShown = true;

        // Built on first use, not at startup: most sessions never open it, and this is a tray
        // app whose whole point is staying small.
        if (_settingsPanel is null)
        {
            _settingsPanel = new SettingsPanel(App.Settings, () => Refresh());
            _settingsPanel.CloseRequested += HideSettings;
            SettingsLayer.Children.Add(_settingsPanel);
        }

        HidePalette();
        SettingsLayer.Visibility = Visibility.Visible;

        Motion.To(SettingsLayer, OpacityProperty, 1, Motion.Medium, Motion.Enter);
        Motion.To(SettingsShift, TranslateTransform.XProperty, 0, Motion.Long, Motion.Enter);
        Motion.To(Motion.Transforms(MainContent).Shift, TranslateTransform.XProperty, -24, Motion.Long, Motion.Enter);

        _settingsPanel.PlayEntrance();

        // Focus follows the user into the layer. Left on the gear underneath, its focus ring —
        // drawn in the adorner layer, above everything — would show straight through the panel.
        _settingsPanel.FocusFirst();
    }

    public void HideSettings()
    {
        if (!_settingsShown) return;
        _settingsShown = false;

        Motion.To(SettingsShift, TranslateTransform.XProperty, 28, Motion.Short, Motion.Exit);
        Motion.To(Motion.Transforms(MainContent).Shift, TranslateTransform.XProperty, 0, Motion.Medium, Motion.Enter);
        Motion.To(SettingsLayer, OpacityProperty, 0, Motion.Short, Motion.Exit,
                  done: () => SettingsLayer.Visibility = Visibility.Collapsed);

        SettingsButton.Focus();
    }

    /// <summary>Escape backs out of whatever is on top; Ctrl+K opens the command palette.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (SheetOpen) { _sheet!.Close(); e.Handled = true; return; }
            if (PaletteOpen) { HidePalette(); e.Handled = true; return; }
            if (SettingsOpen) { HideSettings(); e.Handled = true; return; }
        }

        if (!SheetOpen &&
            e.Key == System.Windows.Input.Key.K &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 &&
            !SettingsOpen)
        {
            if (PaletteOpen) HidePalette(); else ShowPalette();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    // ── command palette ──────────────────────────────────────────────────────

    private bool _paletteShown;

    public bool PaletteOpen => _paletteShown;

    /// <summary>Drops in the type-to-switch card, focused and pre-filled with every account.</summary>
    public void ShowPalette()
    {
        if (_items.Count == 0 || _paletteShown || SheetOpen) return;
        _paletteShown = true;

        PaletteBox.Text = "";
        FilterPalette("");
        PaletteLayer.Visibility = Visibility.Visible;

        var (scale, shift) = Motion.Transforms(PaletteCard);
        Motion.To(PaletteBackdrop, OpacityProperty, 1, Motion.Short, Motion.Standard);
        Motion.To(PaletteCard, OpacityProperty, 1, Motion.Short, Motion.Enter);
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.Medium, Motion.Enter, from: 0.95);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Medium, Motion.Enter, from: 0.95);
        Motion.To(shift, TranslateTransform.YProperty, 0, Motion.Medium, Motion.Enter, from: -12);

        PaletteBox.Focus();
    }

    public void HidePalette()
    {
        if (!_paletteShown) return;
        _paletteShown = false;

        var (scale, _) = Motion.Transforms(PaletteCard);
        Motion.To(PaletteBackdrop, OpacityProperty, 0, Motion.Short, Motion.Exit);
        Motion.To(scale, ScaleTransform.ScaleXProperty, 0.97, Motion.Quick, Motion.Exit);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 0.97, Motion.Quick, Motion.Exit);
        Motion.To(PaletteCard, OpacityProperty, 0, Motion.Quick, Motion.Exit,
                  done: () => PaletteLayer.Visibility = Visibility.Collapsed);
    }

    private void FilterPalette(string query)
    {
        query = query.Trim();

        // Match on the REAL name/email, not the redacted label — you can still find an account
        // by typing it even while the screen is masked for a stream.
        var matches = string.IsNullOrEmpty(query)
            ? _items.AsEnumerable()
            : _items.Where(i =>
                i.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.Profile.Email.Contains(query, StringComparison.OrdinalIgnoreCase));

        PaletteList.ItemsSource = matches.ToList();
        if (PaletteList.Items.Count > 0) PaletteList.SelectedIndex = 0;
    }

    private void PaletteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PalettePlaceholder.Visibility = PaletteBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterPalette(PaletteBox.Text);
    }

    private void PaletteBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Down:
                Move(1); e.Handled = true; break;
            case System.Windows.Input.Key.Up:
                Move(-1); e.Handled = true; break;
            case System.Windows.Input.Key.Enter:
                CommitPalette(); e.Handled = true; break;
        }

        void Move(int by)
        {
            var count = PaletteList.Items.Count;
            if (count == 0) return;
            PaletteList.SelectedIndex = (PaletteList.SelectedIndex + by + count) % count;
            PaletteList.ScrollIntoView(PaletteList.SelectedItem);
        }
    }

    private void PaletteList_Click(object sender, MouseButtonEventArgs e)
    {
        if (PaletteList.SelectedItem is not null) CommitPalette();
    }

    private void PaletteBackdrop_Click(object sender, MouseButtonEventArgs e)
    {
        // Only a click on the dimmed backdrop itself dismisses; clicks inside the card bubble up
        // here too, so ignore anything that landed on a real control.
        if (ReferenceEquals(e.OriginalSource, PaletteBackdrop)) HidePalette();
    }

    private void CommitPalette()
    {
        var target = PaletteList.SelectedItem as AccountItem;
        HidePalette();
        if (target is not null && !target.IsActive) SwitchTo(target.Profile);
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

        // Note where every card sits, so the rebuilt list can glide from there (see ListAnimator).
        _listAnimator.Capture();

        _items.Clear();
        foreach (var p in profiles)
        {
            var isActive = !string.IsNullOrEmpty(activeUuid)
                           && string.Equals(p.AccountUuid, activeUuid, StringComparison.OrdinalIgnoreCase);

            _items.Add(new AccountItem(p)
            {
                IsActive = isActive,

                // Flagged up front so a dead profile is visible before it's switched into,
                // instead of surfacing as Claude Code's sign-in screen afterwards. The account in
                // use is judged by the live credentials Claude Code keeps fresh — our copy only
                // catches up at the next switch, and judging by it flagged a perfectly healthy
                // active account as expired.
                NeedsReauth = isActive ? !LiveCredentialsUsable() : !IsProfileUsable(p),

                Compact = App.Settings.Compact,
            });
        }

        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ActiveAccountText.Text = activeEmail is null
            ? Loc.T("app.notSignedIn")
            : Loc.T("app.activePrefix", Redactor.Mask(activeEmail));
        ActiveDot.Visibility = activeEmail is null ? Visibility.Collapsed : Visibility.Visible;

        // An account that is logged in but not yet saved is the main thing a new user needs to do.
        var currentIsSaved = _items.Any(i => i.IsActive);
        SaveCurrentButton.IsEnabled = activeEmail is not null && !currentIsSaved;

        _listAnimator.Play(ActiveItem?.Profile.Id);

        // Feeds the optional Claude Code status line, which can't read ~/.claude.json itself.
        ClaudeCodeIntegration.WriteActiveLabel(
            _items.FirstOrDefault(i => i.IsActive)?.DisplayName ?? activeEmail ?? "");

        App.Tray?.Rebuild(_items.ToList());
        App.Mini?.UpdateFrom(ActiveItem);

        _ = RefreshUsageAsync(force: false);
    }

    /// <summary>The account Claude Code is currently using, or null. Drives the tray and mini pill.</summary>
    internal AccountItem? ActiveItem => _items.FirstOrDefault(i => i.IsActive);

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
                if (force) ShowToast("Couldn't fetch usage (rate limit or connection). Try again shortly.", ToastKind.Warning);
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
        App.Mini?.UpdateFrom(active);

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

                // A refresh that just worked is the best proof there is that the account is alive.
                item.NeedsReauth = !AccountSwitcher.CredentialsUsable(updated);
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

    /// <summary>Health of the credentials Claude Code is using right now. Never throws.</summary>
    private static bool LiveCredentialsUsable()
    {
        try
        {
            return File.Exists(ClaudePaths.CredentialsFile) &&
                   AccountSwitcher.CredentialsUsable(File.ReadAllText(ClaudePaths.CredentialsFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;   // mid-write or locked: not knowing is no reason to cry wolf
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
        UpdateProgress.Visibility = Visibility.Collapsed;

        if (UpdateBanner.Visibility == Visibility.Visible) return;
        UpdateBanner.Visibility = Visibility.Visible;
        if (IsVisible) Motion.Rise(UpdateBanner, 10, Motion.Long, fromScale: 0.98);
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
        UpdateProgress.Percent = 0;
        UpdateProgress.Visibility = Visibility.Visible;

        var progress = new Progress<double>(fraction =>
        {
            UpdateProgress.Percent = Math.Clamp(fraction, 0, 1) * 100;
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
                UpdateProgress.Visibility = Visibility.Collapsed;
                UpdateButton.Content = Loc.T("update.openPage");
                UpdateButton.IsEnabled = true;
                _update = null;
                return;
            }

            _stagedUpdate = staged;
            UpdateBody.Text = Loc.T("update.ready");
            UpdateButton.Content = Loc.T("update.restart");
            UpdateButton.IsEnabled = true;
            Motion.Pop(UpdateButton, 0.9, Motion.Medium);
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
            ShowToast(Loc.T("switch.done", Redactor.Mask(profile.DisplayName)) + " " + note, ToastKind.Success);

            if (!silent && App.Settings.SwitchNotifications)
                App.Tray?.Notify(Loc.T("switch.title"), $"{profile.DisplayName}\n{note}");

            // Only warn when the account is genuinely un-restorable — i.e. its REFRESH token has
            // expired. An expired ACCESS token is normal and self-heals: Claude Code (and our own
            // 10-minute background refresh) renew it from the refresh token. Checking the access
            // token here was the old false alarm that told users to re-add perfectly good accounts.
            if (!AccountSwitcher.CredentialsUsable(secret.CredentialsJson))
            {
                ShowToast($"{profile.DisplayName}: the saved sign-in has expired. " +
                          "Use \"Sign in\" on its card to renew it.", ToastKind.Warning);
                App.Tray?.Notify("Re-sign-in needed",
                    $"{profile.DisplayName}'s saved sign-in expired. Sign in to it again.");
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

    /// <summary>The card's one button: switch to the account, or sign back into it if its sign-in died.</summary>
    private void UseButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AccountItem item) return;

        if (item.NeedsReauth) OpenAddAccount(item.Profile.Email);
        else SwitchTo(item.Profile);
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
                if (announce) ShowToast("No signed-in session to save. Sign into Claude Code first.", ToastKind.Warning);
                return null;
            }

            var (profile, secret) = captured.Value;

            var existing = FindSaved(profile);
            if (existing is not null) KeepUserFields(profile, existing);

            profile.LastUsedAt = DateTimeOffset.UtcNow;
            _store.Save(profile, secret);

            Refresh();

            if (announce)
            {
                ShowToast(existing is not null
                    ? $"{Redactor.Mask(profile.DisplayName)} updated."
                    : $"{Redactor.Mask(profile.DisplayName)} saved.", ToastKind.Success);
            }

            return profile;
        }
        catch (Exception ex)
        {
            ShowError("Save failed", ex);
            return null;
        }
    }

    private void AddAccountButton_Click(object sender, RoutedEventArgs e) => OpenAddAccount();

    /// <summary>True while the add-account sheet is up.</summary>
    private bool SheetOpen => _sheet?.IsOpen == true;

    /// <summary>
    /// Opens the add-account sheet. <paramref name="email"/> turns it into "sign in again" for an
    /// account whose saved sign-in died, and hints that address to the sign-in page.
    /// </summary>
    internal void OpenAddAccount(string? email = null)
    {
        HidePalette();
        HideSettings();

        // Snapshot the live account first so it stays switchable even if the user never saved
        // it by hand. Nothing about it is modified by the login.
        SaveCurrentAccount(announce: false);

        if (_sheet is null)
        {
            _sheet = new AddAccountSheet { Save = SaveAddedAccount };
            _sheet.Closed += OnSheetClosed;
            SheetLayer.Children.Add(_sheet);
        }

        _sheet.Open(email);
    }

    /// <summary>The account the sheet just saved, lit up once the sheet is out of the way.</summary>
    private string? _highlightAfterSheet;

    private void OnSheetClosed()
    {
        AddAccountButton.Focus();

        if (_highlightAfterSheet is { } id)
        {
            _highlightAfterSheet = null;
            _listAnimator.Highlight(id);
        }
    }

    /// <summary>Stores what the sheet captured and tells it what that amounted to.</summary>
    private (AddAccountSheet.Outcome Outcome, string Name) SaveAddedAccount(Profile profile, ProfileSecret secret)
    {
        // Re-adding a known account refreshes its tokens rather than duplicating the row.
        var existing = FindSaved(profile);

        var isActiveAccount = !string.IsNullOrEmpty(profile.AccountUuid) && string.Equals(
            profile.AccountUuid, CurrentAccountUuid(), StringComparison.OrdinalIgnoreCase);

        if (existing is not null) KeepUserFields(profile, existing);

        _store.Save(profile, secret);
        Refresh();
        _highlightAfterSheet = profile.Id;

        if (existing is null)
        {
            App.Tray?.Notify(Loc.T("add.addedTitle"), profile.DisplayName);
            return (AddAccountSheet.Outcome.Added, profile.DisplayName);
        }

        return (isActiveAccount ? AddAccountSheet.Outcome.AlreadyActive : AddAccountSheet.Outcome.Renewed,
                profile.DisplayName);
    }

    private Profile? FindSaved(Profile fresh) => _store.LoadAll().FirstOrDefault(p =>
        !string.IsNullOrEmpty(p.AccountUuid) &&
        string.Equals(p.AccountUuid, fresh.AccountUuid, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A freshly captured profile knows only what the credentials say. Everything the user chose
    /// — name, colour, auto-switch exclusion — and the usage history belongs to the saved one and
    /// must survive a re-save; losing them on every "sign in again" would punish the user for
    /// fixing an expired account.
    /// </summary>
    private static void KeepUserFields(Profile fresh, Profile saved)
    {
        fresh.Id = saved.Id;
        fresh.Label = saved.Label;
        fresh.CreatedAt = saved.CreatedAt;
        fresh.LastUsedAt = saved.LastUsedAt;
        fresh.Color = saved.Color;
        fresh.ExcludeFromAuto = saved.ExcludeFromAuto;

        fresh.UsageFiveHourPercent = saved.UsageFiveHourPercent;
        fresh.UsageFiveHourResetsAt = saved.UsageFiveHourResetsAt;
        fresh.UsageSevenDayPercent = saved.UsageSevenDayPercent;
        fresh.UsageSevenDayResetsAt = saved.UsageSevenDayResetsAt;
        fresh.UsageFetchedAt = saved.UsageFetchedAt;
        fresh.UsageHistory = saved.UsageHistory;
    }

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
        delete.SetResourceReference(ForegroundProperty, "Danger");
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
            // A swatch beside each name: picking a colour by its name alone is guesswork.
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 10,
                Height = 10,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(ThemeManager.Parse(accent.Base)),
            });
            header.Children.Add(new TextBlock { Text = accent.Name, VerticalAlignment = VerticalAlignment.Center });

            var entry = new MenuItem
            {
                Header = header,
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

        // The card leaves first; the rest of the list then closes the gap (see ListAnimator).
        _listAnimator.Remove(item, () =>
        {
            _store.Delete(item.Profile.Id);
            Refresh();
            ShowToast($"{Redactor.Mask(item.Profile.DisplayName)} deleted.");
        });
    }

    // ── toast ───────────────────────────────────────────────────────────────

    private enum ToastKind { Info, Success, Warning, Error }

    private DispatcherTimer? _toastTimer;

    /// <summary>
    /// Shows a short message that rises in above the footer and leaves by itself — replacing the
    /// old status line, which sat in the footer until the next message and pushed it around.
    /// </summary>
    private void ShowToast(string message, ToastKind kind = ToastKind.Info)
    {
        ToastText.Text = message;

        // The toast is always dark, so its icons use colours bright enough for a dark ground
        // rather than the theme's (which, in light mode, are tuned for white).
        var (glyph, color) = kind switch
        {
            ToastKind.Success => ("", "#6CC49A"),
            ToastKind.Warning => ("", "#E9BC6E"),
            ToastKind.Error => ("", "#F28C82"),
            _ => ("", "#F2A585"),
        };
        ToastIcon.Text = glyph;
        ToastIcon.Foreground = new SolidColorBrush(ThemeManager.Parse(color));

        var (_, shift) = Motion.Transforms(Toast);
        if (Toast.Visibility == Visibility.Visible && Toast.Opacity > 0.5)
        {
            // Already up: a fresh message nudges it rather than replaying the entrance.
            Motion.Pop(Toast, 0.96, Motion.Medium);
        }
        else
        {
            Toast.Visibility = Visibility.Visible;
            Motion.To(Toast, OpacityProperty, 1, Motion.Short, Motion.Enter, from: 0);
            Motion.To(shift, TranslateTransform.YProperty, 0, Motion.Long, Motion.Enter, from: 18);
        }

        // Long enough to read: a base plus a little per character, capped.
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Clamp(2200 + message.Length * 45, 2600, 9000)),
        };
        _toastTimer.Tick += (_, _) =>
        {
            if (Toast.IsMouseOver) return;   // someone is reading it; look again next tick
            HideToast();
        };
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer?.Stop();

        var (_, shift) = Motion.Transforms(Toast);
        Motion.To(shift, TranslateTransform.YProperty, 10, Motion.Short, Motion.Exit);
        Motion.To(Toast, OpacityProperty, 0, Motion.Short, Motion.Exit,
                  done: () => Toast.Visibility = Visibility.Collapsed);
    }

    private void Toast_Click(object sender, MouseButtonEventArgs e) => HideToast();

    private void ShowError(string title, Exception ex)
    {
        ShowToast($"{title}: {ex.Message}", ToastKind.Error);

        // A toast in a hidden window reaches nobody; the tray balloon does.
        if (!IsVisible) App.Tray?.Notify(title, ex.Message);
    }
}

/// <summary>View model wrapper: a profile plus whether it is the account Claude Code is using.</summary>
internal sealed class AccountItem : INotifyPropertyChanged
{
    private bool _isActive;

    public AccountItem(Profile profile) => Profile = profile;

    public Profile Profile { get; }

    /// <summary>The real name, used for switching, ranking, and notifications.</summary>
    public string DisplayName => Profile.DisplayName;

    /// <summary>What the card actually shows — masked when redaction is on. Bind the UI to this.</summary>
    public string DisplayLabel => Redactor.Mask(Profile.DisplayName);

    public string Initial => Profile.Initial;
    public string PlanBadge => Profile.PlanBadge;

    /// <summary>No plan known means no badge, rather than a lonely dash.</summary>
    public Visibility PlanVisibility => PlanBadge == "—" ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Right-aligned hint in the command palette: active state or plan.</summary>
    public string PaletteHint => IsActive ? Loc.T("card.active") : PlanBadge == "—" ? "" : PlanBadge;

    private bool _needsReauth;

    /// <summary>Stored credentials are unusable — the account has to be signed into again.</summary>
    public bool NeedsReauth
    {
        get => _needsReauth;
        set
        {
            if (_needsReauth == value) return;
            _needsReauth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(ActionKind));
        }
    }

    /// <summary>Compact mode collapses the usage panel for a denser list.</summary>
    public bool Compact { get; init; }

    public Visibility UsageVisibility => Compact ? Visibility.Collapsed : Visibility.Visible;

    public string Subtitle
    {
        get
        {
            // The most important thing to say about this account, so it replaces the usual detail.
            if (NeedsReauth) return Loc.T("reauth.subtitle");

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Profile.Email) && Profile.Email != DisplayName)
                parts.Add(Redactor.Mask(Profile.Email));

            // Personal accounts get an auto-generated "<email>'s Organization" that just
            // repeats the email — noise, so leave it out.
            var org = Profile.OrganizationName;
            if (!string.IsNullOrWhiteSpace(org) &&
                !org.StartsWith(Profile.Email, StringComparison.OrdinalIgnoreCase))
                parts.Add(Redactor.Mask(org));

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
            OnPropertyChanged(nameof(ActionKind));
            OnPropertyChanged(nameof(AvatarBrush));
            OnPropertyChanged(nameof(AvatarTextBrush));
        }
    }

    /// <summary>
    /// What the card's action area shows: "active" (a chip, no button), "reauth" (the button signs
    /// back in), or "switch". A dead sign-in outranks being active — that account can't be used
    /// until it's fixed, active or not.
    /// </summary>
    public string ActionKind => NeedsReauth ? "reauth" : IsActive ? "active" : "switch";

    /// <summary>
    /// The row button's label. A property rather than a template trigger because a trigger's
    /// Setter.Value cannot carry the live translation binding {loc:Tr} produces — and one binding
    /// is less machinery than a trigger anyway.
    /// </summary>
    public string ActionLabel => Loc.T(NeedsReauth ? "card.signIn" : IsActive ? "card.active" : "card.switch");

    // ── avatar ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The avatar's fill. Priority: a colour the user pinned, then the accent for the active
    /// account, then an automatic colour derived from the email so even untouched accounts are
    /// distinct. A pinned colour outranks the active tint so the colour you chose is always the
    /// thing you recognise the row by.
    /// </summary>
    public Brush AvatarBrush
    {
        get
        {
            if (ThemeManager.Accents.FirstOrDefault(a => a.Key == Profile.Color) is { Key: not null } accent)
                return new SolidColorBrush(ThemeManager.Parse(accent.Base));

            if (IsActive)
                return Resource("Accent");

            var seed = string.IsNullOrWhiteSpace(Profile.Email) ? DisplayName : Profile.Email;
            return new SolidColorBrush(Identicon.ColorFor(seed));
        }
    }

    // Every avatar now carries a colour (pinned, accent, or identicon), so the initial is always
    // white on it.
    public Brush AvatarTextBrush => Brushes.White;

    /// <summary>The usage ring only means something when there's a usage figure and it's turned on.</summary>
    public bool ShowUsageRing => App.Settings.UsageRings && HasUsage;

    public Visibility ExcludedVisibility =>
        Profile.ExcludeFromAuto ? Visibility.Visible : Visibility.Collapsed;

    private static Brush Resource(string key)
        => (Brush)Application.Current.Resources[key];

    // ── real usage (from /api/oauth/usage) ───────────────────────────────────

    /// <summary>Re-reads the usage fields off the profile after a fetch updated them.</summary>
    public void RefreshUsage()
    {
        foreach (var name in new[]
        {
            nameof(FiveHourReset), nameof(FiveHourBrush), nameof(FiveHourValue),
            nameof(SevenDayReset), nameof(SevenDayBrush), nameof(SevenDayValue),
            nameof(UsageAsOf), nameof(UsageTooltip),
            nameof(SparkPoints), nameof(SparkVisibility),
            nameof(ShowUsageRing), nameof(PendingVisibility), nameof(ValueVisibility),
        })
            OnPropertyChanged(name);
    }

    public bool HasUsage => Profile.UsageFetchedAt is not null;

    /// <summary>"…" placeholder before the first fetch; the live number takes over once it arrives.</summary>
    public Visibility PendingVisibility => HasUsage ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ValueVisibility => HasUsage ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>5-hour utilization as a number; treated as full when unknown, for "most free" ranking.</summary>
    public double FiveHourValue => Profile.UsageFiveHourPercent ?? 100;

    /// <summary>7-day utilization; 0 when unknown so its bar simply reads empty rather than full.</summary>
    public double SevenDayValue => Profile.UsageSevenDayPercent ?? 0;

    // ── sparkline: 5-hour utilization over recent samples, scaled into a 74×16 box ──

    private const double SparkW = 74, SparkH = 16;

    public Visibility SparkVisibility =>
        Profile.UsageHistory.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;

    public PointCollection SparkPoints
    {
        get
        {
            var points = new PointCollection();
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

    public Visibility MostFreeVisibility => _isMostFree ? Visibility.Visible : Visibility.Collapsed;

    public Brush FiveHourBrush => BarBrush(Profile.UsageFiveHourPercent);
    public Brush SevenDayBrush => BarBrush(Profile.UsageSevenDayPercent);

    /// <summary>Green under 70%, amber to 90%, red above — the usual "getting close" cue.</summary>
    private static Brush BarBrush(double? percent)
    {
        var p = percent ?? 0;
        var color = p switch
        {
            >= 90 => Color.FromRgb(0xB4, 0x44, 0x3A),
            >= 70 => Color.FromRgb(0xC9, 0x64, 0x42),
            _ => Color.FromRgb(0x2F, 0x7A, 0x5B),
        };
        return new SolidColorBrush(color);
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
