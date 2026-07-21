using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeSwitch.Core;

/// <summary>
/// A single system-wide hotkey (Ctrl+Alt+S) that fires even when ClaudeSwitch has no focus —
/// so you can cycle accounts from inside your editor without reaching for the tray.
///
/// Uses RegisterHotKey against a hidden message hook on the given window. If the combo is already
/// taken by another app, registration simply fails and the feature is quietly unavailable.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xB001;

    // Modifiers
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private readonly Action _onPressed;
    private bool _registered;

    private GlobalHotkey(IntPtr hwnd, HwndSource source, Action onPressed)
    {
        _hwnd = hwnd;
        _source = source;
        _onPressed = onPressed;
        _source.AddHook(WndProc);
    }

    /// <summary>Registers Ctrl+Alt+S on <paramref name="window"/>. Returns null if it can't hook.</summary>
    public static GlobalHotkey? Register(Window window, Action onPressed)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return null;

        var source = HwndSource.FromHwnd(hwnd);
        if (source is null) return null;

        var hk = new GlobalHotkey(hwnd, source, onPressed);
        const uint vkS = 0x53;   // 'S'
        hk._registered = RegisterHotKey(hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, vkS);
        return hk._registered ? hk : null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            _onPressed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(_hwnd, HotkeyId);
        _source?.RemoveHook(WndProc);
    }
}
