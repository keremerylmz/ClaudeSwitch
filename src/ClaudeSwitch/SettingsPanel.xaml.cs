using System.Windows;
using System.Windows.Controls;
using ClaudeSwitch.Core;
using Cursors = System.Windows.Input.Cursors;

namespace ClaudeSwitch;

/// <summary>
/// Preferences, shown as a layer over the main view rather than in a window of its own —
/// a separate window for eight toggles reads as a context switch the task doesn't deserve.
///
/// Every change applies immediately and persists; there is no separate "apply" step, and
/// nothing here can leave the app in a half-configured state.
/// </summary>
public partial class SettingsPanel : System.Windows.Controls.UserControl
{
    private readonly AppSettings _settings;
    private readonly Action _onChanged;
    private bool _loading;

    /// <summary>Raised by the back arrow and the Done button; the host plays the fade-out.</summary>
    public event Action? CloseRequested;

    internal SettingsPanel(AppSettings settings, Action onChanged)
    {
        InitializeComponent();
        _settings = settings;
        _onChanged = onChanged;

        _loading = true;
        CompactToggle.IsChecked = settings.Compact;
        TrayClickToggle.IsChecked = settings.TrayLeftClickMenu;
        TrayUsageToggle.IsChecked = settings.TrayMenuUsage;
        AutoSwitchToggle.IsChecked = settings.AutoSwitch;
        NotifyToggle.IsChecked = settings.LimitNotifications;
        SwitchNotifyToggle.IsChecked = settings.SwitchNotifications;
        SessionsToggle.IsChecked = settings.ShowLiveSessions;
        StartupToggle.IsChecked = StartupManager.IsEnabled();
        HotkeyToggle.IsChecked = settings.GlobalHotkey;
        UpdatesToggle.IsChecked = settings.CheckForUpdates;

        // Integration state comes from Claude Code's own settings file, not from ours: the user
        // may have removed either one by hand, and the switch has to tell the truth about that.
        StatusLineToggle.IsChecked = ClaudeCodeIntegration.StatusLineState() == ClaudeCodeIntegration.State.Ours;
        LimitHookToggle.IsChecked = ClaudeCodeIntegration.LimitHookState() == ClaudeCodeIntegration.State.Ours;
        _loading = false;

        BuildPillLists();
        Localize();
    }

    // ── appearance ──────────────────────────────────────────────────────────

    private void SelectTheme(string mode)
    {
        if (mode == _settings.ThemeMode) return;
        _settings.ThemeMode = mode;
        _settings.Save();

        // Crossfade every open window from the old theme into the new one.
        var windows = Application.Current.Windows.Cast<Window>().Where(w => w.IsVisible).ToList();
        ThemeTransition.Crossfade(windows, () =>
        {
            ThemeManager.ApplyMode(mode);
            _settings.Save();   // ThemeManager resolved "system"; persist the matching DarkMode flag
        });

        BuildPillLists();
    }

    private void CompactToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Compact = CompactToggle.IsChecked == true;
        _settings.Save();

        // Crossfade the main window as its list relayouts with/without the usage panels.
        if (App.MainView is { } main)
            ThemeTransition.Crossfade(new[] { main }, () => main.Refresh());
    }

    // ── accounts ────────────────────────────────────────────────────────────

    private void TrayClickToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.TrayLeftClickMenu = TrayClickToggle.IsChecked == true;
        _settings.Save();
    }

    private void TrayUsageToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.TrayMenuUsage = TrayUsageToggle.IsChecked == true;
        _settings.Save();
        _onChanged();   // rebuilds the tray menu
    }

    private void SelectSort(string sort)
    {
        if (sort == _settings.AccountSort) return;
        _settings.AccountSort = sort;
        _settings.Save();
        BuildPillLists();
        _onChanged();
    }

    // ── behavior ────────────────────────────────────────────────────────────

    private void AutoSwitchToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoSwitch = AutoSwitchToggle.IsChecked == true;
        _settings.Save();
    }

    private void SelectThreshold(int percent)
    {
        if (percent == _settings.AutoSwitchThreshold) return;
        _settings.AutoSwitchThreshold = percent;
        _settings.Save();
        BuildPillLists();
    }

    private void NotifyToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.LimitNotifications = NotifyToggle.IsChecked == true;
        _settings.Save();
    }

    private void SwitchNotifyToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.SwitchNotifications = SwitchNotifyToggle.IsChecked == true;
        _settings.Save();
    }

    private void SessionsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ShowLiveSessions = SessionsToggle.IsChecked == true;
        _settings.Save();
    }

    private void StartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.StartWithWindows = StartupToggle.IsChecked == true;
        _settings.Save();
        StartupManager.Set(_settings.StartWithWindows);
    }

    private void HotkeyToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.GlobalHotkey = HotkeyToggle.IsChecked == true;
        _settings.Save();
        App.MainView?.ApplyHotkeySetting();
    }

    private void UpdatesToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.CheckForUpdates = UpdatesToggle.IsChecked == true;
        _settings.Save();
    }

    // ── Claude Code integration ─────────────────────────────────────────────

    private void StatusLineToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Integration(StatusLineToggle,
            ClaudeCodeIntegration.InstallStatusLine,
            ClaudeCodeIntegration.RemoveStatusLine);
    }

    private void LimitHookToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Integration(LimitHookToggle,
            ClaudeCodeIntegration.InstallLimitHook,
            ClaudeCodeIntegration.RemoveLimitHook);
    }

    /// <summary>
    /// Runs an install/uninstall against ~/.claude/settings.json. On failure the switch snaps
    /// back, because a toggle that reads "on" while nothing was written is worse than an error.
    /// </summary>
    private void Integration(System.Windows.Controls.CheckBox toggle, Action install, Action remove)
    {
        var wantOn = toggle.IsChecked == true;

        try
        {
            if (wantOn) install(); else remove();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Integration", ex);
            MessageBox.Show(Window.GetWindow(this)!, ex.Message, Loc.T("settings.integration"),
                MessageBoxButton.OK, MessageBoxImage.Warning);

            _loading = true;
            toggle.IsChecked = !wantOn;
            _loading = false;
        }
    }

    // ── pill lists ──────────────────────────────────────────────────────────

    private void BuildPillLists()
    {
        Fill(ThemeList,
        [
            ("light", Loc.T("settings.themeLight")),
            ("dark", Loc.T("settings.themeDark")),
            ("system", Loc.T("settings.themeSystem")),
        ], _settings.ThemeMode, SelectTheme);

        Fill(SortList,
        [
            ("recent", Loc.T("settings.sortRecent")),
            ("name", Loc.T("settings.sortName")),
            ("free", Loc.T("settings.sortFree")),
            ("plan", Loc.T("settings.sortPlan")),
        ], _settings.AccountSort, SelectSort);

        Fill(ThresholdList,
            [.. new[] { 85, 90, 95, 98 }.Select(p => (p.ToString(), $"{p}%"))],
            _settings.AutoSwitchThreshold.ToString(),
            key => SelectThreshold(int.Parse(key)));

        LanguageList.Items.Clear();
        foreach (var lang in Loc.Languages)
            LanguageList.Items.Add(BuildPill(lang.Name, lang.Code == Loc.Current, () => SelectLanguage(lang.Code)));
    }

    private static void Fill(ItemsControl list, (string Key, string Label)[] options,
                             string selected, Action<string> onSelect)
    {
        list.Items.Clear();
        foreach (var (key, label) in options)
            list.Items.Add(BuildPill(label, key == selected, () => onSelect(key)));
    }

    /// <summary>
    /// Colours are set with SetResourceReference, not FindResource: a pill built with the
    /// latter keeps the brush it captured and stays light after a switch to dark mode.
    /// </summary>
    private static Border BuildPill(string label, bool selected, Action onClick)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var pill = new Border
        {
            Child = text,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(13, 8, 13, 8),
            Margin = new Thickness(3),
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(selected ? 1.4 : 1),
        };

        text.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Accent" : "Text");
        pill.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSoft" : "Bg");
        pill.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentBorder" : "Border");

        pill.MouseLeftButtonUp += (_, _) => onClick();
        return pill;
    }

    // ── language ────────────────────────────────────────────────────────────

    private void SelectLanguage(string code)
    {
        if (code == Loc.Current) return;

        _settings.Language = code;
        _settings.Save();
        Loc.Current = code;      // raises Loc.Changed → main window rebuilds

        BuildPillLists();        // re-highlight selection and re-label the pills
        Localize();              // re-translate this window in place
        _onChanged();
    }

    /// <summary>Applies the current language to this panel's own labels.</summary>
    private void Localize()
    {
        TitleText.Text = Loc.T("settings.title");
        BackButton.ToolTip = Loc.T("settings.done");

        AppearanceHeader.Text = Loc.T("settings.appearance");
        ThemeTitle.Text = Loc.T("settings.theme");
        ThemeDesc.Text = Loc.T("settings.themeDesc");
        CompactTitle.Text = Loc.T("settings.compact");
        CompactDesc.Text = Loc.T("settings.compactDesc");

        AccountsHeader.Text = Loc.T("settings.accounts");
        SortTitle.Text = Loc.T("settings.sort");
        SortDesc.Text = Loc.T("settings.sortDesc");
        TrayClickTitle.Text = Loc.T("settings.trayClick");
        TrayClickDesc.Text = Loc.T("settings.trayClickDesc");
        TrayUsageTitle.Text = Loc.T("settings.trayUsage");
        TrayUsageDesc.Text = Loc.T("settings.trayUsageDesc");

        BehaviorHeader.Text = Loc.T("settings.behavior");
        AutoSwitchTitle.Text = Loc.T("settings.autoSwitch");
        AutoSwitchDesc.Text = Loc.T("settings.autoSwitchDesc");
        ThresholdTitle.Text = Loc.T("settings.threshold");
        ThresholdDesc.Text = Loc.T("settings.thresholdDesc");
        NotifyTitle.Text = Loc.T("settings.notifications");
        NotifyDesc.Text = Loc.T("settings.notificationsDesc");
        SwitchNotifyTitle.Text = Loc.T("settings.switchNotifications");
        SwitchNotifyDesc.Text = Loc.T("settings.switchNotificationsDesc");
        SessionsTitle.Text = Loc.T("settings.sessions");
        SessionsDesc.Text = Loc.T("settings.sessionsDesc");
        StartupTitle.Text = Loc.T("settings.startup");
        StartupDesc.Text = Loc.T("settings.startupDesc");
        HotkeyTitle.Text = Loc.T("settings.hotkey");
        HotkeyDesc.Text = Loc.T("settings.hotkeyDesc");
        UpdatesTitle.Text = Loc.T("settings.updates");
        UpdatesDesc.Text = Loc.T("settings.updatesDesc");

        IntegrationHeader.Text = Loc.T("settings.integration");
        StatusLineTitle.Text = Loc.T("settings.statusLine");
        StatusLineDesc.Text = Loc.T("settings.statusLineDesc");
        LimitHookTitle.Text = Loc.T("settings.limitHook");
        LimitHookDesc.Text = Loc.T("settings.limitHookDesc");
        IntegrationNote.Text = Loc.T("settings.integrationNote");

        LanguageHeader.Text = Loc.T("settings.language");
        DoneButton.Content = Loc.T("settings.done");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
