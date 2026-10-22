using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WowDualBoxAssist;

/// <summary>
/// Global WH_KEYBOARD_LL and WH_MOUSE_LL hooks. Must be installed from a thread that pumps messages
/// (the WinForms UI thread); the callbacks then run on that same thread. The hooks only observe and,
/// for the swap key, swallow: nothing is ever generated or forwarded.
/// </summary>
internal sealed class InputHooks : IDisposable
{
    private readonly Native.HookProc _keyboardProc;   // held in fields so the GC never collects the delegates
    private readonly Native.HookProc _mouseProc;
    private IntPtr _keyboard;
    private IntPtr _mouse;

    /// <summary>Return true to swallow the event so no other window sees it.</summary>
    public Func<InputEvent, bool>? Handler { get; set; }

    public InputHooks()
    {
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
    }

    public void InstallKeyboard()
    {
        if (_keyboard != IntPtr.Zero) return;
        _keyboard = Install(Native.WH_KEYBOARD_LL, _keyboardProc, "keyboard");
    }

    /// <summary>
    /// The mouse hook sees every mouse move, so it is only installed while a mouse button is the
    /// swap key (or one is being picked).
    /// </summary>
    public void SetMouseHook(bool on)
    {
        if (on && _mouse == IntPtr.Zero)
        {
            _mouse = Install(Native.WH_MOUSE_LL, _mouseProc, "mouse");
        }
        else if (!on && _mouse != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_mouse);
            _mouse = IntPtr.Zero;
        }
    }

    private static IntPtr Install(int kind, Native.HookProc proc, string what)
    {
        var handle = Native.SetWindowsHookEx(kind, proc, Native.GetModuleHandle(null), 0);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not install the {what} hook.");
        return handle;
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Handler is not null)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            var e = new InputEvent(HotkeyKind.Key, (int)k.vkCode, IsUp: (k.flags & Native.LLKHF_UP) != 0);
            if (Dispatch(e)) return (IntPtr)1;
        }
        return Native.CallNextHookEx(_keyboard, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Handler is not null)
        {
            // Moves and left/right clicks fall straight through; only the bindable buttons are looked at.
            uint msg = (uint)wParam.ToInt64();
            InputEvent? e = msg switch
            {
                Native.WM_MBUTTONDOWN => new InputEvent(HotkeyKind.Mouse, Hotkey.MouseMiddle, IsUp: false),
                Native.WM_MBUTTONUP => new InputEvent(HotkeyKind.Mouse, Hotkey.MouseMiddle, IsUp: true),
                Native.WM_XBUTTONDOWN or Native.WM_XBUTTONUP => XButton(lParam, msg == Native.WM_XBUTTONUP),
                _ => null,
            };
            if (e is { } ev && Dispatch(ev)) return (IntPtr)1;
        }
        return Native.CallNextHookEx(_mouse, nCode, wParam, lParam);
    }

    private static InputEvent? XButton(IntPtr lParam, bool up)
    {
        var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
        int button = (int)(m.mouseData >> 16);   // XBUTTON1 = 1, XBUTTON2 = 2
        return button is 1 or 2 ? new InputEvent(HotkeyKind.Mouse, Hotkey.MouseX1 + button - 1, up) : null;
    }

    private bool Dispatch(InputEvent e)
    {
        try { return Handler!(e); }
        catch { return false; }   // never let an exception escape into the hook chain
    }

    public void Dispose()
    {
        SetMouseHook(false);
        if (_keyboard == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_keyboard);
        _keyboard = IntPtr.Zero;
    }
}
