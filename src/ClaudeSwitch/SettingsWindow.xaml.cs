using System.Windows;
using System.Windows.Controls;
using ClaudeSwitch.Core;
using Cursors = System.Windows.Input.Cursors;

namespace ClaudeSwitch;

/// <summary>
/// Preferences: dark mode, compact mode, and interface language. Every change applies
/// immediately and persists — there is no separate "apply" step.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _onChanged;
    private bool _loading;

    internal SettingsWindow(AppSettings settings, Action onChanged)
    {
        InitializeComponent();
        _settings = settings;
        _onChanged = onChanged;

        _loading = true;
        DarkToggle.IsChecked = settings.DarkMode;
        CompactToggle.IsChecked = settings.Compact;
        AutoSwitchToggle.IsChecked = settings.AutoSwitch;
        NotifyToggle.IsChecked = settings.LimitNotifications;
        StartupToggle.IsChecked = StartupManager.IsEnabled();
        HotkeyToggle.IsChecked = settings.GlobalHotkey;
        UpdatesToggle.IsChecked = settings.CheckForUpdates;
        _loading = false;

        BuildLanguageList();
        Localize();

        SourceInitialized += (_, _) => WindowChrome.Apply(this, ThemeManager.IsDark);
    }

    // ── appearance ──────────────────────────────────────────────────────────

    private void DarkToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.DarkMode = DarkToggle.IsChecked == true;
        _settings.Save();

        // Crossfade every open window from the old theme into the new one.
        var windows = Application.Current.Windows.Cast<Window>().Where(w => w.IsVisible).ToList();
        ThemeTransition.Crossfade(windows, () => ThemeManager.Apply(_settings.DarkMode));
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

    // ── behavior ────────────────────────────────────────────────────────────

    private void AutoSwitchToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoSwitch = AutoSwitchToggle.IsChecked == true;
        _settings.Save();
    }

    private void NotifyToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.LimitNotifications = NotifyToggle.IsChecked == true;
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

    // ── language ────────────────────────────────────────────────────────────

    private void BuildLanguageList()
    {
        LanguageList.Items.Clear();
        foreach (var lang in Loc.Languages)
            LanguageList.Items.Add(BuildLanguagePill(lang));
    }

    private Border BuildLanguagePill(Loc.Language lang)
    {
        var selected = lang.Code == Loc.Current;

        var text = new TextBlock
        {
            Text = lang.Name,
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

        // SetResourceReference (not FindResource) so the colours track live theme changes —
        // otherwise a pill keeps the brush captured at build time and stays light in dark mode.
        text.SetResourceReference(TextBlock.ForegroundProperty, selected ? "Accent" : "Text");
        pill.SetResourceReference(Border.BackgroundProperty, selected ? "AccentSoft" : "Bg");
        pill.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentBorder" : "Border");

        pill.MouseLeftButtonUp += (_, _) => SelectLanguage(lang.Code);
        return pill;
    }

    private void SelectLanguage(string code)
    {
        if (code == Loc.Current) return;

        _settings.Language = code;
        _settings.Save();
        Loc.Current = code;      // raises Loc.Changed → main window rebuilds

        BuildLanguageList();     // re-highlight selection
        Localize();              // re-translate this window in place
        _onChanged();
    }

    /// <summary>Applies the current language to this window's own labels.</summary>
    private void Localize()
    {
        Title = Loc.T("settings.title");
        TitleText.Text = Loc.T("settings.title");
        AppearanceHeader.Text = Loc.T("settings.appearance");
        DarkTitle.Text = Loc.T("settings.darkMode");
        DarkDesc.Text = Loc.T("settings.darkModeDesc");
        CompactTitle.Text = Loc.T("settings.compact");
        CompactDesc.Text = Loc.T("settings.compactDesc");

        BehaviorHeader.Text = Loc.T("settings.behavior");
        AutoSwitchTitle.Text = Loc.T("settings.autoSwitch");
        AutoSwitchDesc.Text = Loc.T("settings.autoSwitchDesc");
        NotifyTitle.Text = Loc.T("settings.notifications");
        NotifyDesc.Text = Loc.T("settings.notificationsDesc");
        StartupTitle.Text = Loc.T("settings.startup");
        StartupDesc.Text = Loc.T("settings.startupDesc");
        HotkeyTitle.Text = Loc.T("settings.hotkey");
        HotkeyDesc.Text = Loc.T("settings.hotkeyDesc");
        UpdatesTitle.Text = Loc.T("settings.updates");
        UpdatesDesc.Text = Loc.T("settings.updatesDesc");

        LanguageHeader.Text = Loc.T("settings.language");
        DoneButton.Content = Loc.T("settings.done");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
