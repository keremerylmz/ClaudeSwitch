using System.Windows;
using ClaudeSwitch.Core;
using Brush = System.Windows.Media.Brush;

namespace ClaudeSwitch;

/// <summary>
/// The mini-mode pill: a small, always-on-top window showing just the active account's usage
/// ring, name, and 5-hour figure. It keeps usage glanceable without opening the full window or
/// hovering the tray, and nothing else in the app offers that surface.
///
/// It owns no data or timers — the main window stays alive (hidden) and pushes updates here via
/// <see cref="UpdateFrom"/>, so the pill is a pure view.
/// </summary>
public partial class MiniWindow : Window
{
    public MiniWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => WindowChrome.ApplyBackdrop(this, mica: false);   // just rounds the corners
        Loaded += (_, _) => RestorePosition();
        LocationChanged += (_, _) => SavePosition();
    }

    /// <summary>Mirrors the active account onto the pill; a null account means nobody is signed in.</summary>
    internal void UpdateFrom(AccountItem? active)
    {
        if (active is null)
        {
            NameText.Text = Loc.T("app.notSignedIn");
            UsageText.Text = "";
            AvatarText.Text = "?";
            Avatar.Background = (Brush)FindResource("SurfaceHi");
            Ring.ShowRing = false;
            return;
        }

        NameText.Text = active.DisplayLabel;
        AvatarText.Text = active.Initial;
        Avatar.Background = active.AvatarBrush;

        if (active.HasUsage)
        {
            Ring.ShowRing = App.Settings.UsageRings;
            Ring.Percent = active.FiveHourValue;
            Ring.ProgressBrush = active.FiveHourBrush;
            UsageText.Text = Loc.T("mini.fiveHour", (int)active.FiveHourValue);
        }
        else
        {
            Ring.ShowRing = false;
            UsageText.Text = "…";
        }
    }

    private void Pill_Drag(object sender, MouseButtonEventArgs e)
    {
        // The restore button handles its own click; a drag anywhere else on the pill moves it.
        if (e.OriginalSource is FrameworkElement { Name: "RestoreButton" }) return;
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private void Restore_Click(object sender, RoutedEventArgs e) => App.ExitMiniMode();

    private void RestorePosition()
    {
        var s = App.Settings;
        if (s.MiniLeft is { } left && s.MiniTop is { } top && OnAScreen(left, top))
        {
            Left = left;
            Top = top;
            return;
        }

        // First run: tuck into the working area's bottom-right, above the tray.
        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;
    }

    private void SavePosition()
    {
        if (!IsLoaded) return;
        App.Settings.MiniLeft = Left;
        App.Settings.MiniTop = Top;
        App.Settings.Save();
    }

    private static bool OnAScreen(double left, double top)
    {
        var r = new System.Drawing.Rectangle((int)left, (int)top, 40, 40);
        return System.Windows.Forms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r));
    }
}
