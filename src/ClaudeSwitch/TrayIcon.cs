using System.Drawing;
using System.Windows.Forms;

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
        _menu = new ContextMenuStrip { ShowImageMargin = false };

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "ClaudeSwitch",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _icon.DoubleClick += (_, _) => App.ShowMain();
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
        _menu.Items.Clear();

        if (accounts.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("No saved accounts") { Enabled = false });
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
                };

                var target = account.Profile;
                item.Click += (_, _) => App.MainView?.SwitchTo(target);
                _menu.Items.Add(item);
            }
        }

        _menu.Items.Add(new ToolStripSeparator());

        var manage = new ToolStripMenuItem("Manage accounts…");
        manage.Click += (_, _) => App.ShowMain();
        _menu.Items.Add(manage);

        var exit = new ToolStripMenuItem("Exit");
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
