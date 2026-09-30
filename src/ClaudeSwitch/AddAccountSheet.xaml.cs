using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClaudeSwitch.Core;

namespace ClaudeSwitch;

/// <summary>
/// Adding (or re-adding) an account, start to finish, in one sheet over the main window.
///
/// The user picks how the sign-in page reaches them — straight into the default browser, or as
/// a link to paste wherever the right claude.ai account is signed in — and the sheet then waits
/// for the credentials to land, verifies them, and hands them to the host to save. Exactly one
/// sign-in page is ever opened (see <see cref="LoginSession"/> for how the CLI's own browser tab
/// is avoided), and closing the sheet at any point kills the login and its scratch directory.
/// </summary>
public partial class AddAccountSheet : System.Windows.Controls.UserControl
{
    internal enum Outcome { Added, Renewed, AlreadyActive }

    private enum Page { Choose, Preparing, Waiting, Success, Error }
    private enum Method { Browser, Copy }

    private static readonly TimeSpan UrlTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Generous: signing in can mean an email code, SSO, or finding the right browser.</summary>
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Persists a captured account and says what that amounted to. Supplied by the host.</summary>
    internal Func<Profile, ProfileSecret, (Outcome Outcome, string Name)>? Save;

    /// <summary>Raised once the sheet has finished animating away.</summary>
    public event Action? Closed;

    private Page _page = Page.Choose;
    private Method? _method;
    private string? _email;
    private LoginSession? _session;
    private LoginSession.SignInUrls _urls;
    private CancellationTokenSource? _cancel;
    private DispatcherTimer? _autoClose;
    private DispatcherTimer? _copiedReset;
    private bool _closing;

    public AddAccountSheet()
    {
        InitializeComponent();
    }

    public bool IsOpen { get; private set; }

    /// <summary>A login is under way — a stray click on the backdrop must not throw it away.</summary>
    private bool Busy => _page is Page.Preparing or Page.Waiting;

    private IEnumerable<FrameworkElement> Pages => [ChoosePage, PreparingPage, WaitingPage, SuccessPage, ErrorPage];

    // ── open / close ────────────────────────────────────────────────────────

    /// <summary>
    /// Slides the sheet up. <paramref name="email"/> marks a re-sign-in for that account and is
    /// passed on to the sign-in page as a hint.
    /// </summary>
    internal void Open(string? email = null)
    {
        if (IsOpen && !_closing) return;

        IsOpen = true;
        _closing = false;
        _email = string.IsNullOrWhiteSpace(email) ? null : email;
        _method = null;

        var reauth = _email is not null;
        ChooseTitle.Text = Loc.T(reauth ? "add.reauthTitle" : "add.title");
        ChooseBody.Text = reauth ? Loc.T("add.reauthBody", Redactor.Mask(_email)) : Loc.T("add.body");
        ChooseIcon.Text = reauth ? "" : "";
        RememberBox.IsChecked = false;

        // A remembered choice skips the question and goes straight to work.
        Method? remembered = App.Settings.SignInMethod switch
        {
            "browser" => Method.Browser,
            "copy" => Method.Copy,
            _ => null,
        };

        ShowPage(remembered is null ? Page.Choose : Page.Preparing, animate: false);

        Visibility = Visibility.Visible;
        Motion.To(Backdrop, OpacityProperty, 1, Motion.Short, Motion.Standard);
        Motion.To(SheetHost, OpacityProperty, 1, Motion.Short, Motion.Standard);
        Motion.To(SheetShift, TranslateTransform.YProperty, 0, Motion.Long, Motion.Enter, from: 56);

        // Keyboard focus comes along into the sheet; left on the button underneath, that
        // button's focus ring would be drawn right over the sheet.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_page == Page.Choose) BrowserOption.Focus();
            else Focus();
        });

        if (remembered is { } method) Begin(method);
    }

    /// <summary>Cancels whatever is in flight and slides the sheet away.</summary>
    internal void Close()
    {
        if (!IsOpen || _closing) return;
        _closing = true;

        // The login's own finally disposes the session and its scratch dir. Letting go of the
        // token here, rather than there, means a reopened sheet can start afresh immediately.
        var cancel = _cancel;
        _cancel = null;
        cancel?.Cancel();
        _autoClose?.Stop();

        Motion.To(Backdrop, OpacityProperty, 0, Motion.Short, Motion.Exit);
        Motion.To(SheetShift, TranslateTransform.YProperty, 44, Motion.Short, Motion.Exit);
        Motion.To(SheetHost, OpacityProperty, 0, Motion.Short, Motion.Exit, done: () =>
        {
            Visibility = Visibility.Collapsed;
            IsOpen = false;
            _closing = false;
            StopPing();
            Closed?.Invoke();
        });
    }

    // ── the login ───────────────────────────────────────────────────────────

    private async void Begin(Method method)
    {
        // One login at a time: a double-click on an option must not start two.
        if (_cancel is not null) return;

        _method = method;
        CodeRow.Visibility = Visibility.Collapsed;
        CodeBox.Clear();

        if (RememberBox.IsChecked == true)
        {
            App.Settings.SignInMethod = method == Method.Browser ? "browser" : "copy";
            App.Settings.Save();
        }

        if (!ClaudeCli.IsInstalled)
        {
            ShowError(Loc.T("add.errorCli"));
            return;
        }

        ShowPage(Page.Preparing);

        var cancel = new CancellationTokenSource();
        _cancel = cancel;
        LoginSession? session = null;

        try
        {
            session = LoginSession.Start(_email);
            _session = session;

            _urls = await session.WaitForUrlsAsync(UrlTimeout, cancel.Token);
            if (_urls.Best is null)
            {
                ShowError(Loc.T("add.errorStart"), session.Diagnostic);
                return;
            }

            var delivered = method == Method.Browser ? OpenLink() : CopyLink();
            ShowWaiting(method, delivered);

            if (await WaitForAccountAsync(session, cancel.Token) is not { } account) return;

            var (outcome, name) = Save?.Invoke(account.Profile, account.Secret)
                                  ?? (Outcome.Added, account.Profile.DisplayName);
            ShowSuccess(outcome, name);
        }
        catch (OperationCanceledException)
        {
            // Closed by the user; Close() already took care of the sheet.
        }
        catch (Exception ex)
        {
            CrashLog.Write("AddAccount", ex);
            if (!cancel.IsCancellationRequested) ShowError(ex.Message);
        }
        finally
        {
            session?.Dispose();
            if (ReferenceEquals(_session, session)) _session = null;

            cancel.Dispose();
            if (ReferenceEquals(_cancel, cancel)) _cancel = null;
        }
    }

    /// <summary>
    /// Waits for the new account's credentials to appear in the scratch directory — the real
    /// completion signal, more dependable than the CLI's console text — and proves the token
    /// works before handing it over.
    /// </summary>
    private async Task<(Profile Profile, ProfileSecret Secret)?> WaitForAccountAsync(
        LoginSession session, CancellationToken token)
    {
        var switcher = new AccountSwitcher();
        var deadline = DateTime.UtcNow + LoginTimeout;
        DateTime? exitedAt = null;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(700, token);

            if (switcher.CaptureFromConfigDir(session.ConfigDir) is { } captured)
            {
                WaitingTitle.Text = Loc.T("add.finishing");

                // A credential set that looks complete but is rejected means the CLI is still
                // settling; saving it would produce a profile that silently fails later.
                var access = UsageApi.ExtractAccessToken(captured.Secret.CredentialsJson);
                if (access is not null)
                {
                    var (_, status) = await UsageApi.FetchWithStatusAsync(access, token);
                    if (status == UsageApi.FetchStatus.Unauthorized) continue;
                }

                return captured;
            }

            if (session.HasExited)
            {
                // Give the output pumps and the final file writes a moment before concluding.
                exitedAt ??= DateTime.UtcNow;
                var grace = session.ReportedSuccess ? TimeSpan.FromSeconds(6) : TimeSpan.FromSeconds(1.5);
                if (DateTime.UtcNow - exitedAt > grace)
                {
                    ShowError(Loc.T("add.errorExited"), session.Diagnostic);
                    return null;
                }
            }
        }

        ShowError(Loc.T("add.errorTimeout"));
        return null;
    }

    /// <summary>Opens the sign-in page in the default browser — the user's own, not a private one.</summary>
    private bool OpenLink()
    {
        if (_urls.Best is not { } url) return false;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            if (_urls.NeedsCode) RevealCodeRow();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Puts the sign-in link on the clipboard. Retried briefly: another app holding the clipboard
    /// open makes a single attempt fail for no reason the user could act on.
    /// </summary>
    private bool CopyLink()
    {
        if (_urls.Best is not { } url) return false;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(url);
                if (_urls.NeedsCode) RevealCodeRow();
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(30);
            }
        }

        return false;
    }

    private void SubmitCode()
    {
        var code = CodeBox.Text.Trim();
        if (code.Length == 0 || _session is null)
        {
            Motion.Shake(CodeBox);
            return;
        }

        _session.SubmitCode(code);
        CodeBox.Clear();
        WaitingTitle.Text = Loc.T("add.finishing");
    }

    // ── pages ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Swaps pages with a crossfade while the sheet's height glides to fit the new content,
    /// so the sheet reshapes itself rather than jumping.
    /// </summary>
    private void ShowPage(Page page, bool animate = true)
    {
        var target = PageElement(page);
        if (page == _page && target.Visibility == Visibility.Visible && animate) return;

        var previous = PageElement(_page);
        _page = page;

        if (!animate || !IsVisible)
        {
            foreach (var p in Pages)
            {
                p.Visibility = ReferenceEquals(p, target) ? Visibility.Visible : Visibility.Collapsed;
                Motion.Set(p, OpacityProperty, 1);
            }

            Motion.Set(Motion.Transforms(target).Shift, TranslateTransform.YProperty, 0);
            SyncPing();
            return;
        }

        Motion.MorphHeight(Sheet, () =>
        {
            if (!ReferenceEquals(previous, target)) previous.Visibility = Visibility.Collapsed;
            target.Visibility = Visibility.Visible;
        });
        Motion.Rise(target, 10, Motion.Medium);
        SyncPing();
    }

    private FrameworkElement PageElement(Page page) => page switch
    {
        Page.Preparing => PreparingPage,
        Page.Waiting => WaitingPage,
        Page.Success => SuccessPage,
        Page.Error => ErrorPage,
        _ => ChoosePage,
    };

    private void ShowWaiting(Method method, bool delivered)
    {
        WaitingIcon.Text = method == Method.Browser ? "" : "";
        WaitingTitle.Text = Loc.T("add.waitingTitle");

        WaitingBody.Text = (method, delivered) switch
        {
            (Method.Browser, true) => Loc.T("add.waitingBrowser"),
            (Method.Browser, false) => Loc.T("add.errorBrowser"),
            (Method.Copy, true) => Loc.T("add.waitingCopy"),
            _ => Loc.T("add.copyDesc"),
        };
        WaitingBody.SetResourceReference(TextBlock.ForegroundProperty,
            method == Method.Browser && !delivered ? "Danger" : "TextSecondary");

        ResetCopyButton();
        ShowPage(Page.Waiting);

        if (method == Method.Copy && delivered) FlashCopied();
        // Whichever way failed, the other button is the way forward — point at it.
        if (!delivered) Motion.Shake(CopyLinkButton);
    }

    private void ShowSuccess(Outcome outcome, string name)
    {
        var shown = Redactor.Mask(name);
        var same = outcome == Outcome.AlreadyActive;

        SuccessTitle.Text = Loc.T(same ? "add.sameTitle"
                                : outcome == Outcome.Renewed ? "add.renewedTitle"
                                : "add.addedTitle");
        SuccessBody.Text = same ? Loc.T("add.sameBody", shown) : shown;
        SuccessDisc.SetResourceReference(Shape.FillProperty, same ? "WarningSoft" : "SuccessSoft");
        SuccessCheck.Visibility = same ? Visibility.Collapsed : Visibility.Visible;
        SameIcon.Visibility = same ? Visibility.Visible : Visibility.Collapsed;

        ShowPage(Page.Success);

        // The badge pops, then the tick draws itself in.
        Motion.Pop(SuccessBadge, 0.4, Motion.Long);
        Motion.Set(SuccessCheck, Shape.StrokeDashOffsetProperty, 9);
        Motion.To(SuccessCheck, Shape.StrokeDashOffsetProperty, 0, TimeSpan.FromMilliseconds(460),
                  Motion.Enter, delay: TimeSpan.FromMilliseconds(170));

        // The browser has done its job; coming back here is the natural next step.
        if (Window.GetWindow(this) is { } window) WindowFocus.Raise(window);

        // "That's the account you're already using" needs reading; everything else can go by itself.
        if (!same) StartAutoClose();
    }

    private void ShowError(string message, string? detail = null)
    {
        ErrorBody.Text = message;
        ErrorDetail.Text = detail ?? "";
        ErrorDetail.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;

        ShowPage(Page.Error);
        Motion.Shake(ErrorBadge);

        if (Window.GetWindow(this) is { IsActive: false } window) WindowFocus.Raise(window);
    }

    private void StartAutoClose()
    {
        _autoClose?.Stop();
        _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _autoClose.Tick += (_, _) =>
        {
            _autoClose?.Stop();

            // Someone pointing at the sheet is still reading it; they'll press Done.
            if (_page == Page.Success && !SheetHost.IsMouseOver) Close();
        };
        _autoClose.Start();
    }

    private void RevealCodeRow()
    {
        if (CodeRow.Visibility == Visibility.Visible) return;

        if (_page == Page.Waiting)
            Motion.MorphHeight(Sheet, () => CodeRow.Visibility = Visibility.Visible);
        else
            CodeRow.Visibility = Visibility.Visible;
    }

    // ── small motions ───────────────────────────────────────────────────────

    /// <summary>A slow radar ping behind the waiting icon — running only while it can be seen.</summary>
    private void SyncPing()
    {
        if (_page != Page.Waiting || !Motion.Enabled)
        {
            StopPing();
            return;
        }

        var period = new Duration(TimeSpan.FromMilliseconds(1900));
        var grow = new DoubleAnimation(0.85, 1.6, period)
        {
            EasingFunction = Motion.Enter,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        var fade = new DoubleAnimation(0.26, 0, period) { RepeatBehavior = RepeatBehavior.Forever };

        PingScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        PingScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Ping.BeginAnimation(OpacityProperty, fade);
    }

    private void StopPing()
    {
        PingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Ping.BeginAnimation(OpacityProperty, null);
        Ping.Opacity = 0;
    }

    /// <summary>"Copy link" turns into a green "Copied" for a moment.</summary>
    private void FlashCopied()
    {
        CopyLinkIcon.Text = "";
        CopyLinkText.Text = Loc.T("add.copied");
        CopyLinkIcon.SetResourceReference(TextBlock.ForegroundProperty, "Success");
        CopyLinkText.SetResourceReference(TextBlock.ForegroundProperty, "Success");
        Motion.Pop(CopyLinkIcon, 0.4, Motion.Medium);

        _copiedReset?.Stop();
        _copiedReset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _copiedReset.Tick += (_, _) => { _copiedReset?.Stop(); ResetCopyButton(); };
        _copiedReset.Start();
    }

    private void ResetCopyButton()
    {
        CopyLinkIcon.Text = "";
        CopyLinkText.Text = Loc.T("add.copyLink");
        CopyLinkIcon.ClearValue(TextBlock.ForegroundProperty);
        CopyLinkText.ClearValue(TextBlock.ForegroundProperty);
    }

    // ── input ───────────────────────────────────────────────────────────────

    private void BrowserOption_Click(object sender, RoutedEventArgs e) => Begin(Method.Browser);

    private void CopyOption_Click(object sender, RoutedEventArgs e) => Begin(Method.Copy);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_method is { } method) Begin(method);
        else ShowPage(Page.Choose);
    }

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Busy) Close();
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (CopyLink()) FlashCopied();
        else Motion.Shake(CopyLinkButton);
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (OpenLink()) return;

        WaitingBody.Text = Loc.T("add.errorBrowser");
        WaitingBody.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        Motion.Shake(OpenBrowserButton);
    }

    private void SubmitCode_Click(object sender, RoutedEventArgs e) => SubmitCode();

    private void CodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) SubmitCode();
    }
}
