namespace WowDualBoxAssist;

/// <summary>What one press of the swap key should do.</summary>
public readonly record struct SwapDecision(bool Swallow, IntPtr Target);

public static class SwapLogic
{
    /// <summary>
    /// In one game, go to the other. In any other window, go back to the game used last, but only
    /// when <paramref name="fromAnyWindow"/> is on; otherwise the key is left alone for that window.
    /// While a game is in front the key is always kept out of it, even with only one game open.
    /// </summary>
    public static SwapDecision Decide(IntPtr foreground, IntPtr main, IntPtr alt, IntPtr lastGame, bool fromAnyWindow)
    {
        bool inMain = foreground != IntPtr.Zero && foreground == main;
        bool inAlt = foreground != IntPtr.Zero && foreground == alt;
        if (inMain || inAlt)
            return new SwapDecision(true, inMain ? alt : main);

        if (!fromAnyWindow) return new SwapDecision(false, IntPtr.Zero);

        IntPtr back = lastGame != IntPtr.Zero && (lastGame == main || lastGame == alt)
            ? lastGame
            : main != IntPtr.Zero ? main : alt;
        return new SwapDecision(back != IntPtr.Zero, back);
    }
}

/// <summary>How a switch went: straight away, only via the input-sharing fallback, or not at all.</summary>
public enum SwitchResult { Direct, Fallback, Failed }

internal static class WindowActivator
{
    /// <summary>Brings a window to the front, the way clicking its taskbar button would. Sends it no input.</summary>
    public static SwitchResult BringToFront(IntPtr hWnd)
    {
        if (!Native.IsWindow(hWnd)) return SwitchResult.Failed;
        if (Native.IsIconic(hWnd)) Native.ShowWindow(hWnd, Native.SW_RESTORE);
        if (Native.SetForegroundWindow(hWnd) && Native.GetForegroundWindow() == hWnd) return SwitchResult.Direct;

        // Windows only lets the owner of the current foreground window hand focus on. Sharing that
        // window's input state for a moment satisfies the rule without synthesising any input.
        IntPtr front = Native.GetForegroundWindow();
        uint frontThread = front == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(front, out _);
        uint ownThread = Native.GetCurrentThreadId();
        bool attached = frontThread != 0 && frontThread != ownThread && Native.AttachThreadInput(ownThread, frontThread, true);
        try
        {
            Native.BringWindowToTop(hWnd);
            Native.SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(ownThread, frontThread, false);
        }
        return Native.GetForegroundWindow() == hWnd ? SwitchResult.Fallback : SwitchResult.Failed;
    }
}

/// <summary>Reports every change of foreground window (EVENT_SYSTEM_FOREGROUND) on the UI thread.</summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private readonly Native.WinEventProc _proc;   // held in a field so the GC never collects the delegate
    private IntPtr _handle;

    public Action<IntPtr>? Changed { get; set; }

    public ForegroundWatcher()
    {
        _proc = (_, _, hWnd, _, _, _, _) => Changed?.Invoke(hWnd);
    }

    public void Start()
    {
        if (_handle != IntPtr.Zero) return;
        _handle = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        Native.UnhookWinEvent(_handle);
        _handle = IntPtr.Zero;
    }
}
