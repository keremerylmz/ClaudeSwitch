using System.Windows;
using ClaudeSwitch.Core;

namespace ClaudeSwitch;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    /// <summary>Set only by the tray's Exit command, so window closes can be treated as "hide".</summary>
    internal static bool IsShuttingDown { get; private set; }

    internal static TrayIcon? Tray { get; private set; }

    /// <summary>Not named "Main" — that collides with the generated WPF entry point.</summary>
    internal static MainWindow? MainView { get; private set; }

    /// <summary>App-wide preferences (theme, compact, language). Loaded once at startup.</summary>
    internal static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // A second instance could race the first one writing credentials — allow only one.
        _singleInstance = new Mutex(initiallyOwned: true, "ClaudeSwitch.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("ClaudeSwitch is already running. Check the notification area icon.",
                "ClaudeSwitch", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Dates and numbers are formatted with a fixed en-US culture regardless of the chosen
        // interface language — keeps "26 Jul 08:59" stable rather than following the OS locale.
        var ui = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = ui;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = ui;
        Thread.CurrentThread.CurrentCulture = ui;
        Thread.CurrentThread.CurrentUICulture = ui;

        // WPF bindings format via the element's Language, which defaults to the OS locale.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(
                System.Windows.Markup.XmlLanguage.GetLanguage(ui.IetfLanguageTag)));

        // Install before anything else can throw.
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write("UI", args.Exception);
            args.Handled = true;   // a failed menu click must not take the whole app down
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{args.Exception.Message}\n\n" +
                $"Details were saved to:\n{CrashLog.LogPath}",
                "ClaudeSwitch", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) CrashLog.Write("Fatal", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("Background", args.Exception);
            args.SetObserved();
        };

        if (!ClaudePaths.ClaudeCodeIsInstalled)
        {
            MessageBox.Show(
                "Claude Code configuration not found (~/.claude.json).\n\n" +
                "Run Claude Code once and sign in before using ClaudeSwitch.",
                "ClaudeSwitch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        ClaudePaths.EnsureAppDirectories();

        // Apply saved preferences BEFORE any window is built, so the first paint is already in
        // the right theme and language (no flash of the default light/English UI).
        Settings = AppSettings.Load();
        Loc.Current = Settings.Language;
        ThemeManager.ApplyMode(Settings.ThemeMode);

        // "Follow Windows" has to keep following: the OS theme flips on a schedule for plenty of
        // people, and a tray app that stays light until its next restart is the whole complaint.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
            if (ThemeManager.Mode != "system") return;

            Current.Dispatcher.BeginInvoke(() =>
            {
                if (ThemeManager.IsDark == ThemeManager.WindowsPrefersDark()) return;
                ThemeManager.Reapply();
                MainView?.Refresh();   // re-resolves the brushes the cards build in code
            });
        };

        // Temp browser profiles from previous logins are still locked when a login finishes,
        // so they get cleared here instead.
        PrivateBrowser.SweepOldProfiles();

        // If we just restarted into a freshly installed build, the previous exe is sitting next
        // to us as ".old" — deletable now that nothing is running from it.
        Updater.SweepOldBuilds();

        MainView = new MainWindow();
        Tray = new TrayIcon();

        // --minimized lets a startup shortcut boot straight into the tray.
        var startHidden = e.Args.Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        if (!startHidden) MainView.Show();
        else MainView.Refresh();   // populate the tray menu without showing the window

        // Keep the Run key in step with the setting (e.g. if the exe was moved).
        if (Settings.StartWithWindows) StartupManager.Set(true);

        _ = CheckForUpdatesAsync();
    }

    /// <summary>
    /// Floor between update checks. This app lives in the tray for days at a time, so checking
    /// only at startup means a long-running instance never learns about a release at all — but
    /// GitHub rate-limits anonymous callers, so it is polled sparingly.
    /// </summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromMinutes(15);

    private static DateTimeOffset _lastUpdateCheck = DateTimeOffset.MinValue;
    private static bool _updateOffered;

    /// <summary>
    /// Checks for a newer release unless one was already found or we checked recently.
    /// Called at startup, on the periodic refresh, and whenever the window is brought up.
    /// </summary>
    internal static async Task CheckForUpdatesAsync()
    {
        if (!Settings.CheckForUpdates || _updateOffered) return;
        if (DateTimeOffset.UtcNow - _lastUpdateCheck < UpdateCheckInterval) return;
        _lastUpdateCheck = DateTimeOffset.UtcNow;

        var release = await UpdateChecker.CheckAsync();
        if (release is null) return;

        _updateOffered = true;   // the banner stays put; no need to keep asking

        // Both surfaces: the balloon reaches someone whose window is closed, the banner is what
        // they act on. Clicking the balloon brings the window up so the button is right there.
        MainView?.OfferUpdate(release);
        Tray?.NotifyUpdate("ClaudeSwitch", Loc.T("update.available", release.Tag), showMain: true);
    }

    /// <summary>Brings the window back from the tray.</summary>
    internal static void ShowMain()
    {
        if (MainView is null) return;
        MainView.Show();
        if (MainView.WindowState == WindowState.Minimized) MainView.WindowState = WindowState.Normal;
        MainView.Activate();
        MainView.Refresh();

        // Opening the window is the moment an update is worth knowing about — and the throttle
        // inside means this costs nothing when it was just checked.
        _ = CheckForUpdatesAsync();
    }

    /// <summary>Brings the window up and fades the preferences layer in over it.</summary>
    internal static void OpenSettings()
    {
        if (MainView is null) return;
        ShowMain();
        MainView.ShowSettings();
    }

    internal static void RequestShutdown()
    {
        IsShuttingDown = true;
        Tray?.Dispose();
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
