namespace WowDualBoxAssist;

public enum HotkeyKind { None, Key, Mouse }

/// <summary>
/// The swap key: a keyboard key (Code is the virtual-key code) or a mouse button
/// (Code is <see cref="Hotkey.MouseMiddle"/>, <see cref="Hotkey.MouseX1"/> or <see cref="Hotkey.MouseX2"/>),
/// optionally with Ctrl, Alt and/or Shift held.
/// </summary>
public readonly record struct Hotkey(HotkeyKind Kind, int Code, bool Ctrl, bool Alt, bool Shift)
{
    public const int MouseMiddle = 3;
    public const int MouseX1 = 4;   // usually the "back" side button
    public const int MouseX2 = 5;   // usually the "forward" side button

    public bool IsSet => Kind != HotkeyKind.None;

    /// <summary>The listed modifiers must be held; extra ones are ignored, so the swap still works mid Shift-cast.</summary>
    public bool Matches(InputEvent e, Modifiers held) =>
        IsSet && e.Kind == Kind && e.Code == Code
        && (!Ctrl || held.Ctrl) && (!Alt || held.Alt) && (!Shift || held.Shift);

    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5;

    public static bool IsWindowsKey(int vk) => vk is 0x5B or 0x5C;

    /// <summary>A key that types something, so taking it over costs the user that character (in chat too).</summary>
    public bool IsTypingKey =>
        Kind == HotkeyKind.Key && !Ctrl && !Alt
        && (Code is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2
            || Code == (int)Keys.Space || Code == (int)Keys.Enter || Code == (int)Keys.Back || Code == (int)Keys.Tab);

    /// <summary>What the user gives up by binding this, or null when nothing worth mentioning.</summary>
    public string? Caveat(bool fromAnyWindow)
    {
        if (IsTypingKey)
            return fromAnyWindow
                ? $"{this} won't type anywhere while this app is running, WoW chat included."
                : $"{this} won't type in WoW, chat included, while this app is running.";
        if (fromAnyWindow && Kind == HotkeyKind.Mouse && !Ctrl && !Alt && !Shift)
            return Code == MouseMiddle
                ? "Middle-click won't work in other apps (like opening links in a new tab) while this app is running."
                : "This button's back/forward in your browser won't work while this app is running. " +
                  "Untick \"works from other windows\" to keep it for the browser.";
        return null;
    }

    public override string ToString()
    {
        if (!IsSet) return "Not set";
        string mods = (Ctrl ? "Ctrl + " : "") + (Alt ? "Alt + " : "") + (Shift ? "Shift + " : "");
        return mods + (Kind == HotkeyKind.Mouse ? MouseName(Code) : KeyName(Code));
    }

    private static string MouseName(int code) => code switch
    {
        MouseMiddle => "Middle mouse button",
        MouseX1 => "Mouse button 4",
        MouseX2 => "Mouse button 5",
        _ => $"Mouse button {code}",
    };

    private static string KeyName(int vk)
    {
        var key = (Keys)vk;
        return key switch
        {
            >= Keys.D0 and <= Keys.D9 => ((char)vk).ToString(),
            >= Keys.NumPad0 and <= Keys.NumPad9 => $"Num {vk - (int)Keys.NumPad0}",
            Keys.Oemtilde => "` (backtick)",
            Keys.OemMinus => "-",
            Keys.Oemplus => "=",
            Keys.OemOpenBrackets => "[",
            Keys.OemCloseBrackets => "]",
            Keys.OemPipe => "\\",
            Keys.OemSemicolon => ";",
            Keys.OemQuotes => "'",
            Keys.Oemcomma => ",",
            Keys.OemPeriod => ".",
            Keys.OemQuestion => "/",
            Keys.OemBackslash => "\\ (extra key)",
            Keys.Multiply => "Num *",
            Keys.Add => "Num +",
            Keys.Subtract => "Num -",
            Keys.Divide => "Num /",
            Keys.Decimal => "Num .",
            Keys.Scroll => "Scroll Lock",
            Keys.Capital => "Caps Lock",
            Keys.NumLock => "Num Lock",
            Keys.PageUp => "Page Up",
            Keys.PageDown => "Page Down",
            Keys.Back => "Backspace",
            Keys.Enter => "Enter",
            Keys.Apps => "Menu",
            _ => key.ToString(),
        };
    }
}

public readonly record struct Modifiers(bool Ctrl, bool Alt, bool Shift)
{
    public static Modifiers Read() => new(
        (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0,
        (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0,
        (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0);
}

/// <summary>One physical key or mouse button press or release, as seen by the low-level hooks.</summary>
public readonly record struct InputEvent(HotkeyKind Kind, int Code, bool IsUp);

/// <summary>
/// Watches hook events for the swap key, and records a new one when asked to. Only ever decides
/// whether a press is swallowed; it never produces input. Runs on the UI thread with the hooks.
/// </summary>
internal sealed class HotkeyListener
{
    public Hotkey Hotkey { get; set; }
    public bool Capturing { get; private set; }

    /// <summary>Where Ctrl/Alt/Shift state comes from; replaceable so tests do not depend on the real keyboard.</summary>
    public Func<Modifiers> CurrentModifiers { get; set; } = Modifiers.Read;

    /// <summary>The swap key went down. Return true to keep the press, and its release, from every other window.</summary>
    public Func<bool>? Pressed { get; set; }

    public Action<Hotkey>? Captured { get; set; }
    public Action? CaptureCancelled { get; set; }

    // The press being held down: auto-repeats and the release get the same answer as the press did.
    private (HotkeyKind Kind, int Code, bool Swallowed)? _held;

    public void BeginCapture() => Capturing = true;

    public void CancelCapture()
    {
        if (!Capturing) return;
        Capturing = false;
        CaptureCancelled?.Invoke();
    }

    /// <summary>Returns true to swallow the event.</summary>
    public bool Handle(InputEvent e)
    {
        if (_held is { } held && held.Kind == e.Kind && held.Code == e.Code)
        {
            if (e.IsUp) _held = null;
            return held.Swallowed;
        }
        if (e.IsUp) return false;
        if (Capturing) return Capture(e);
        if (!Hotkey.Matches(e, CurrentModifiers())) return false;

        bool swallow = Pressed?.Invoke() ?? false;
        _held = (e.Kind, e.Code, swallow);
        return swallow;
    }

    private bool Capture(InputEvent e)
    {
        if (e.Kind == HotkeyKind.Key)
        {
            // Modifiers are only ever part of a combo, and the Windows keys belong to Windows.
            if (Hotkey.IsModifierKey(e.Code) || Hotkey.IsWindowsKey(e.Code)) return false;
            if (e.Code == (int)Keys.Escape)
            {
                Capturing = false;
                _held = (e.Kind, e.Code, true);
                CaptureCancelled?.Invoke();
                return true;
            }
        }

        var m = CurrentModifiers();
        Hotkey = new Hotkey(e.Kind, e.Code, m.Ctrl, m.Alt, m.Shift);
        Capturing = false;
        _held = (e.Kind, e.Code, true);   // the key that was just picked must not leak into whatever is in front
        Captured?.Invoke(Hotkey);
        return true;
    }
}
