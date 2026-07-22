using System.Drawing;
using System.Windows.Forms;
using ClaudeSwitch.Core;

namespace ClaudeSwitch;

/// <summary>
/// The one-click surface: a notification-area icon whose menu lists every saved account.
/// This is where the app spends almost all of its life, so it holds no state beyond the
/// menu items themselves.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;

    public TrayIcon()
    {
        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9.5f),
            Renderer = new ThemedMenuRenderer(),
            Padding = new Padding(4),
        };

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "ClaudeSwitch",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _icon.DoubleClick += (_, _) => App.ShowMain();

        // Switching accounts is what this icon is for, so put it on the button people press
        // first. Right-click still works — Windows shows the same menu itself.
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && App.Settings.TrayLeftClickMenu)
                ShowMenuAtCursor();
        };
        // An update balloon is only useful if it lands you on the button that installs it.
        _icon.BalloonTipClicked += (_, _) => { if (_balloonOpensMain) App.ShowMain(); };
    }

    /// <summary>
    /// Opens the menu at the pointer. Without handing the menu the foreground, Windows leaves a
    /// tray-owned popup on screen after you click away — the well-known NotifyIcon quirk.
    /// </summary>
    private void ShowMenuAtCursor()
    {
        _menu.Show(System.Windows.Forms.Cursor.Position);
        SetForegroundWindow(_menu.Handle);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private Icon? _generatedIcon;

    /// <summary>
    /// Colours the tray icon by the active account's 5-hour usage (green → amber → red) and puts
    /// the numbers in the hover tooltip, so the current state is readable without opening anything.
    /// </summary>
    public void SetActiveUsage(double? fiveHourPercent, string? accountName)
    {
        var name = accountName ?? "ClaudeSwitch";
        var tip = fiveHourPercent is { } p
            ? $"{name} — {(int)p}% (5h)"
            : name;
        _icon.Text = tip.Length > 62 ? tip[..62] : tip;

        var fresh = BuildIcon(fiveHourPercent);
        if (fresh is not null)
        {
            _icon.Icon = fresh;
            _generatedIcon?.Dispose();
            _generatedIcon = fresh;
        }
    }

    /// <summary>Draws the switch-arrows tile in a colour that reflects the usage level.</summary>
    private static Icon? BuildIcon(double? fiveHourPercent)
    {
        try
        {
            var tile = fiveHourPercent switch
            {
                >= 90 => Color.FromArgb(0xB4, 0x44, 0x3A),   // red
                >= 70 => Color.FromArgb(0xC9, 0x64, 0x42),   // amber
                null => Color.FromArgb(0xC9, 0x64, 0x42),     // unknown: brand accent
                _ => Color.FromArgb(0x2F, 0x7A, 0x5B),        // green
            };

            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var brush = new SolidBrush(tile);
                FillRounded(g, brush, new Rectangle(2, 2, 28, 28), 8);

                using var pen = new Pen(Color.White, 2.6f)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                };
                g.DrawLine(pen, 9, 12, 22, 12);
                g.DrawLine(pen, 19, 9, 22, 12);
                g.DrawLine(pen, 19, 15, 22, 12);
                g.DrawLine(pen, 22, 20, 9, 20);
                g.DrawLine(pen, 12, 17, 9, 20);
                g.DrawLine(pen, 12, 23, 9, 20);
            }

            var hIcon = bmp.GetHicon();
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void FillRounded(Graphics g, Brush brush, Rectangle r, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    /// <summary>Applies theme colours to the menu shell. Item colours are drawn by the renderer.</summary>
    private void ApplyMenuTheme()
    {
        var dark = ThemeManager.IsDark;
        _menu.BackColor = dark ? Color.FromArgb(0x24, 0x22, 0x20) : Color.White;
        _menu.ForeColor = dark ? Color.FromArgb(0xEC, 0xEA, 0xE5) : Color.FromArgb(0x19, 0x19, 0x18);
    }

    /// <summary>Uses the executable's own icon so there is a single source of truth for branding.</summary>
    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // Fall through to the system default rather than failing startup over an icon.
        }

        return SystemIcons.Application;
    }

    /// <summary>Rebuilds the account list in the menu. Called after every refresh.</summary>
    public void Rebuild(IReadOnlyList<AccountItem> accounts)
    {
        ApplyMenuTheme();
        _menu.Items.Clear();

        if (accounts.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem(Loc.T("tray.noAccounts")) { Enabled = false });
        }
        else
        {
            foreach (var account in accounts)
            {
                var label = account.IsActive
                    ? $"● {account.DisplayName}"
                    : $"    {account.DisplayName}";

                // The point of the menu is picking a target without opening the window, which
                // needs the numbers you would have opened the window to read.
                if (App.Settings.TrayMenuUsage && account.HasUsage)
                    label += $"   —   {(int)account.FiveHourValue}%";

                var item = new ToolStripMenuItem(label)
                {
                    Checked = false,
                    Enabled = !account.IsActive,
                    ToolTipText = account.Subtitle,
                    Padding = new Padding(2, 3, 2, 3),
                };

                var target = account.Profile;
                item.Click += (_, _) => App.MainView?.SwitchTo(target);
                _menu.Items.Add(item);
            }
        }

        _menu.Items.Add(new ToolStripSeparator());

        var manage = new ToolStripMenuItem(Loc.T("tray.manage"));
        manage.Click += (_, _) => App.ShowMain();
        _menu.Items.Add(manage);

        var exit = new ToolStripMenuItem(Loc.T("tray.exit"));
        exit.Click += (_, _) => App.RequestShutdown();
        _menu.Items.Add(exit);

        var active = accounts.FirstOrDefault(a => a.IsActive);
        // NotifyIcon.Text is capped at 63 characters by the shell.
        var tip = active is null ? "ClaudeSwitch" : $"ClaudeSwitch — {active.DisplayName}";
        _icon.Text = tip.Length > 62 ? tip[..62] : tip;
    }

    public void Notify(string title, string message)
    {
        _balloonOpensMain = false;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(3000);
    }

    private bool _balloonOpensMain;

    /// <summary>Shows an update balloon whose click brings up the window and its Update button.</summary>
    public void NotifyUpdate(string title, string message, bool showMain)
    {
        _balloonOpensMain = showMain;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(6000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _generatedIcon?.Dispose();
    }
}

/// <summary>
/// Draws the tray menu to match the app's theme, replacing the flat grey Windows default with a
/// bordered card, a rounded accent highlight, and theme-correct text.
/// </summary>
internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    public ThemedMenuRenderer() : base(new ThemedColors()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var c = ThemedColors.Palette;
        e.TextColor = !e.Item.Enabled ? c.Muted
            : e.Item.Selected ? c.Text
            : c.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;

        var c = ThemedColors.Palette;
        var rect = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
        using var brush = new SolidBrush(c.Hover);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        FillRounded(g, brush, rect, 6);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var c = ThemedColors.Palette;
        using var pen = new Pen(c.Border);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    private static void FillRounded(Graphics g, Brush brush, Rectangle r, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}

/// <summary>Theme colours for the tray menu, resolved live from the current theme.</summary>
internal sealed class ThemedColors : ProfessionalColorTable
{
    internal readonly record struct ThemePalette(Color Bg, Color Border, Color Hover, Color Text, Color Muted);

    public static ThemePalette Palette => ThemeManager.IsDark
        ? new ThemePalette(
            Bg: Color.FromArgb(0x24, 0x22, 0x20),
            Border: Color.FromArgb(0x37, 0x34, 0x2F),
            Hover: Color.FromArgb(0x30, 0x2D, 0x2A),
            Text: Color.FromArgb(0xEC, 0xEA, 0xE5),
            Muted: Color.FromArgb(0x87, 0x82, 0x7A))
        : new ThemePalette(
            Bg: Color.White,
            Border: Color.FromArgb(0xE9, 0xE7, 0xE3),
            Hover: Color.FromArgb(0xF4, 0xF3, 0xF1),
            Text: Color.FromArgb(0x19, 0x19, 0x18),
            Muted: Color.FromArgb(0x8A, 0x86, 0x7E));

    public override Color ToolStripDropDownBackground => Palette.Bg;
    public override Color ImageMarginGradientBegin => Palette.Bg;
    public override Color ImageMarginGradientMiddle => Palette.Bg;
    public override Color ImageMarginGradientEnd => Palette.Bg;
    public override Color MenuBorder => Palette.Border;
    public override Color MenuItemBorder => Palette.Hover;
    public override Color MenuItemSelected => Palette.Hover;
    public override Color SeparatorDark => Palette.Border;
    public override Color SeparatorLight => Palette.Border;
}
