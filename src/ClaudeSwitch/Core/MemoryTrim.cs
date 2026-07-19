using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeSwitch.Core;

/// <summary>
/// Releases the process working set when the app goes back to the tray.
///
/// This is a tray utility that idles for hours between clicks, so keeping a WPF render tree's
/// pages resident is pure waste. EmptyWorkingSet moves them to the standby list; Windows pages
/// them back in on demand, which for a hidden window is essentially never.
///
/// To be precise about what this does and does not do: it lowers the *working set* (the number
/// Task Manager shows), not the amount of memory allocated. Re-showing the window costs a few
/// soft page faults. That trade is right here and wrong for a hot path.
/// </summary>
internal static class MemoryTrim
{
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static void Trim()
    {
        try
        {
            // Hand back anything the managed heap is holding but not using first, otherwise
            // we would only be paging out garbage.
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Not worth caring about: this is an optimisation, not a feature.
        }
    }
}
