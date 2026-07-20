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
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
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
