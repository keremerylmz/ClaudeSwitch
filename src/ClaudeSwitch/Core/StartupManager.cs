using Microsoft.Win32;

namespace ClaudeSwitch.Core;

/// <summary>
/// Toggles "launch on sign-in" via the per-user Run key.
///
/// Chosen over a Startup-folder shortcut or a scheduled task because it needs no elevation, is
/// trivially reversible, and is the mechanism users already recognise in Task Manager's Startup
/// tab. Launches with --minimized so it boots straight into the tray.
/// </summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeSwitch";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is not null) key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A settings toggle failing to persist is not worth surfacing loudly.
        }
    }
}
