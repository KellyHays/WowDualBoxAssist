// Checks the swap decision, the swap-key listener (driven with synthetic hook events, so no real
// input is generated) and settings round-trips. With "--focus <log A> <log B>" it also swaps
// between two running "Fake Game A/B" windows for real and checks neither received any input.
using System.Runtime.InteropServices;
using System.Text.Json;
using WowDualBoxAssist;

static class T
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    static int _failures;

    static void Section(string name) => Console.WriteLine($"-- {name}");

    static void Check(bool ok, string what)
    {
        Console.WriteLine(ok ? $"  ok   {what}" : $"  FAIL {what}");
        if (!ok) _failures++;
    }

    static InputEvent Down(int vk) => new(HotkeyKind.Key, vk, IsUp: false);
    static InputEvent Up(int vk) => new(HotkeyKind.Key, vk, IsUp: true);
    static InputEvent MouseDown(int b) => new(HotkeyKind.Mouse, b, IsUp: false);
    static InputEvent MouseUp(int b) => new(HotkeyKind.Mouse, b, IsUp: true);

    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));   // per-monitor v2, like the app: real pixel coordinates
        Decisions();
        Listener();
        Capture();
        Names();
        SettingsRoundTrip();
        ScanSpeed();
        LayoutMath();
        if (args.Length == 3 && args[0] == "--focus") Focus(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--place") Place(args[1], args[2]);

        Console.WriteLine(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    static void Decisions()
    {
        Section("swap decisions");
        IntPtr main = 10, alt = 20, other = 99, none = IntPtr.Zero;

        Check(SwapLogic.Decide(main, main, alt, main, true) == new SwapDecision(true, alt), "in main -> alt");
        Check(SwapLogic.Decide(alt, main, alt, alt, false) == new SwapDecision(true, main), "in alt -> main, even with other windows off");
        Check(SwapLogic.Decide(other, main, alt, alt, true) == new SwapDecision(true, alt), "other window -> the game used last");
        Check(SwapLogic.Decide(other, main, alt, none, true) == new SwapDecision(true, main), "other window, no game used yet -> main");
        Check(SwapLogic.Decide(other, main, alt, 55, true) == new SwapDecision(true, main), "stale last game is ignored");
        Check(SwapLogic.Decide(other, main, alt, alt, false) == new SwapDecision(false, none), "other window with the option off: key left alone");
        Check(SwapLogic.Decide(main, main, none, main, true) == new SwapDecision(true, none), "only one game, in it: swallowed, nowhere to go");
        Check(SwapLogic.Decide(other, none, alt, none, true) == new SwapDecision(true, alt), "only an alt open: brings it back");
        Check(SwapLogic.Decide(other, none, none, none, true) == new SwapDecision(false, none), "no games: key left alone");
        Check(SwapLogic.Decide(none, none, none, none, true) == new SwapDecision(false, none), "nothing in front and no games");
    }

    static void Listener()
    {
        Section("swap key listener");
        int presses = 0;
        bool answer = true;
        var mods = new Modifiers(false, false, false);
        var l = new HotkeyListener
        {
            Hotkey = new Hotkey(HotkeyKind.Key, (int)Keys.Oemtilde, false, false, false),
            CurrentModifiers = () => mods,
            Pressed = () => { presses++; return answer; },
        };
        int tilde = (int)Keys.Oemtilde;

        Check(l.Handle(Down(tilde)) && presses == 1, "press swallowed and acted on");
        Check(l.Handle(Down(tilde)) && presses == 1, "auto-repeat swallowed, not acted on again");
        Check(l.Handle(Up(tilde)), "release swallowed");
        Check(!l.Handle(Down('1')) && !l.Handle(Up('1')), "other keys pass through");
        Check(!l.Handle(Up(tilde)), "a stray release passes through");

        answer = false;
        Check(!l.Handle(Down(tilde)) && presses == 2, "nothing to do: press passes through");
        Check(!l.Handle(Down(tilde)) && presses == 2, "its repeat passes through too");
        Check(!l.Handle(Up(tilde)), "and its release");
        answer = true;

        mods = new Modifiers(false, false, true);
        Check(l.Handle(Down(tilde)) && presses == 3, "extra Shift held still swaps");
        l.Handle(Up(tilde));
        mods = default;

        l.Hotkey = new Hotkey(HotkeyKind.Key, (int)Keys.F5, true, false, false);
        Check(!l.Handle(Down((int)Keys.F5)) && presses == 3, "Ctrl+F5 needs Ctrl");
        l.Handle(Up((int)Keys.F5));
        mods = new Modifiers(true, false, false);
        Check(l.Handle(Down((int)Keys.F5)) && presses == 4, "Ctrl+F5 with Ctrl");
        l.Handle(Up((int)Keys.F5));
        mods = default;

        l.Hotkey = new Hotkey(HotkeyKind.Mouse, Hotkey.MouseX1, false, false, false);
        Check(l.Handle(MouseDown(Hotkey.MouseX1)) && presses == 5, "mouse button 4 swaps");
        Check(l.Handle(MouseUp(Hotkey.MouseX1)), "its release swallowed");
        Check(!l.Handle(MouseDown(Hotkey.MouseX2)) && presses == 5, "mouse button 5 passes through");
        Check(!l.Handle(Down(Hotkey.MouseX1)), "key with the same code as the button is not the button");

        l.Hotkey = default;
        Check(!l.Handle(Down(0)) && presses == 5, "unset hotkey never fires");
    }

    static void Capture()
    {
        Section("picking a new swap key");
        var mods = new Modifiers(false, false, false);
        Hotkey? captured = null;
        int cancelled = 0, presses = 0;
        var l = new HotkeyListener
        {
            Hotkey = new Hotkey(HotkeyKind.Key, (int)Keys.F1, false, false, false),
            CurrentModifiers = () => mods,
            Pressed = () => { presses++; return true; },
            Captured = h => captured = h,
            CaptureCancelled = () => cancelled++,
        };

        l.BeginCapture();
        Check(!l.Handle(Down(0xA2)) && captured is null, "Ctrl alone is not captured and passes through");
        mods = new Modifiers(true, false, false);
        Check(!l.Handle(Down(0x5B)) && captured is null, "Windows key ignored");
        Check(l.Handle(Down((int)Keys.F1)) && presses == 0, "old swap key does not swap while picking; new key swallowed");
        Check(captured == new Hotkey(HotkeyKind.Key, (int)Keys.F1, true, false, false) && !l.Capturing, "captured Ctrl + F1");
        Check(l.Handle(Up((int)Keys.F1)), "release of the picked key swallowed");
        mods = default;

        captured = null;
        l.BeginCapture();
        Check(l.Handle(Down((int)Keys.Escape)) && cancelled == 1 && !l.Capturing && captured is null, "Esc cancels and is swallowed");
        Check(l.Handle(Up((int)Keys.Escape)), "Esc release swallowed");
        Check(l.Hotkey == new Hotkey(HotkeyKind.Key, (int)Keys.F1, true, false, false), "cancel keeps the old key");

        l.BeginCapture();
        Check(l.Handle(MouseDown(Hotkey.MouseX2)) && captured == new Hotkey(HotkeyKind.Mouse, Hotkey.MouseX2, false, false, false), "mouse button 5 captured");
        Check(l.Handle(MouseUp(Hotkey.MouseX2)), "its release swallowed");

        l.BeginCapture();
        l.CancelCapture();
        Check(cancelled == 2 && !l.Capturing, "Cancel button stops picking");
        l.CancelCapture();
        Check(cancelled == 2, "cancelling twice reports once");
    }

    static void Names()
    {
        Section("names and warnings");
        Check(new Hotkey(HotkeyKind.Key, (int)Keys.Oemtilde, false, false, false).ToString() == "` (backtick)", "backtick name");
        Check(new Hotkey(HotkeyKind.Key, (int)Keys.D1, true, true, true).ToString() == "Ctrl + Alt + Shift + 1", "combo name");
        Check(new Hotkey(HotkeyKind.Mouse, Hotkey.MouseX1, false, false, false).ToString() == "Mouse button 4", "mouse name");
        Check(new Hotkey(HotkeyKind.Key, (int)Keys.F13, false, false, false).ToString() == "F13", "F13 name");
        Check(default(Hotkey).ToString() == "Not set", "unset name");

        Check(new Hotkey(HotkeyKind.Key, (int)Keys.Q, false, false, false).Caveat(true) is not null, "bare Q warns");
        Check(new Hotkey(HotkeyKind.Key, (int)Keys.Oemtilde, false, false, false).Caveat(false) is not null, "bare backtick warns");
        Check(new Hotkey(HotkeyKind.Key, (int)Keys.Q, true, false, false).Caveat(true) is null, "Ctrl+Q does not");
        Check(new Hotkey(HotkeyKind.Key, (int)Keys.F13, false, false, false).Caveat(true) is null, "F13 does not");
        Check(new Hotkey(HotkeyKind.Mouse, Hotkey.MouseX1, false, false, false).Caveat(true) is not null, "mouse 4 warns when it works everywhere");
        Check(new Hotkey(HotkeyKind.Mouse, Hotkey.MouseX1, false, false, false).Caveat(false) is null, "mouse 4 fine when games only");
    }

    static void SettingsRoundTrip()
    {
        Section("settings");
        var s = new Settings { SwapHotkey = new Hotkey(HotkeyKind.Mouse, Hotkey.MouseX2, false, true, false), SwapFromAnyWindow = false, Theme = "Dark" };
        var back = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s, Settings.JsonOptions), Settings.JsonOptions)!;
        Check(back.SwapHotkey == s.SwapHotkey && !back.SwapFromAnyWindow && back.Theme == "Dark", "hotkey and options round-trip");
        Check(!new Settings().SwapHotkey.IsSet && new Settings().SwapFromAnyWindow, "defaults: no key yet, works from other windows");
        var old = JsonSerializer.Deserialize<Settings>("{\"ToggleKey\":145,\"KeyList\":\"1-9\",\"Theme\":\"Light\"}", Settings.JsonOptions)!;
        Check(old.Theme == "Light" && !old.SwapHotkey.IsSet, "unknown old fields are ignored");
    }

    static void ScanSpeed()
    {
        // The window scan runs every second on the thread that also handles the swap key, so a slow
        // scan can hold up a swap press. Checks the steady state, after the process names are cached.
        Section("window scan speed");
        var names = new[] { "Wow", "WowClassic" };
        for (int i = 0; i < 5; i++) WowWindowFinder.Find(names);
        var times = new List<double>();
        for (int i = 0; i < 100; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            WowWindowFinder.Find(names);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        Check(times[50] < 2.0, $"scan median {times[50]:F2} ms, worst {times[^1]:F2} ms (should stay under 2 ms)");
    }

    static void LayoutMath()
    {
        Section("window layouts");
        // The real setup: 1440p main, 1440p to the left, 1080p to the right and 150px lower.
        var main = new MonitorInfo(new Rectangle(0, 0, 2560, 1440), new Rectangle(0, 0, 2560, 1368), true);
        var left = new MonitorInfo(new Rectangle(-2560, 0, 2560, 1440), new Rectangle(-2560, 0, 2560, 1368), false);
        var right = new MonitorInfo(new Rectangle(2560, 150, 1920, 1080), new Rectangle(2560, 150, 1920, 1020), false);
        var three = new[] { right, main, left };   // order Windows lists them in does not matter

        Check(Layouts.Left(three) == left && Layouts.Right(three) == right, "left and right found by position, not by number");
        Check(Layouts.Compute(WindowLayout.BothOnMain, three) == new Placement(main.Bounds, main.Bounds), "both on main: both fill it");
        Check(Layouts.Compute(WindowLayout.AltFillsLeft, three) == new Placement(main.Bounds, left.Bounds), "alt fills the left monitor");
        Check(Layouts.Compute(WindowLayout.AltFillsRight, three) == new Placement(main.Bounds, right.Bounds), "alt fills the right monitor");
        Check(Layouts.Compute(WindowLayout.AltCornerLeft, three).Alt == new Rectangle(-1280, 684, 1280, 684),
            "left corner: bottom-right quarter, touching the main monitor, above the taskbar");
        Check(Layouts.Compute(WindowLayout.AltCornerRight, three).Alt == new Rectangle(2560, 660, 960, 510),
            "right corner: bottom-left quarter, touching the main monitor");
        Check(Layouts.Compute(WindowLayout.None, three) == default, "don't move: nothing placed");

        var onlyMain = new[] { main };
        Check(Layouts.Available(onlyMain).SequenceEqual(new[] { WindowLayout.BothOnMain, WindowLayout.None }), "one monitor: only the main layouts offered");
        Check(Layouts.Compute(WindowLayout.AltFillsLeft, onlyMain).Alt is null, "missing monitor: alt left alone");
        Check(Layouts.Available(three).Count == 6, "three monitors: every layout offered");
        Check(Layouts.Describe(WindowLayout.AltFillsRight, three) == "Alt fills the right monitor (1920×1080)", "names say which monitor");
    }

    static void Place(string logA, string logB)
    {
        Section("moving two fake games through every layout and back");
        var a = FindWindow(null, "Fake Game A");
        var b = FindWindow(null, "Fake Game B");
        if (a == IntPtr.Zero || b == IntPtr.Zero) { Check(false, "fake games are running"); return; }

        var monitors = Layouts.CurrentMonitors();
        // A whole monitor is reached by maximizing on it (WoW's "Windowed (Fullscreen)"); a quarter by
        // an exact size, not maximized. Going corner -> full -> corner checks it switches back and forth.
        var order = Layouts.Available(monitors).Where(l => l != WindowLayout.None).ToList();
        order.AddRange(order.Where(l => l is WindowLayout.AltCornerLeft or WindowLayout.AltCornerRight).ToList());
        order.Add(WindowLayout.BothOnMain);
        foreach (var layout in order)
        {
            var placement = Layouts.Compute(layout, monitors);
            // Same two passes as the app: the second fixes a window that rescaled itself on a monitor
            // with different scaling. The moves are asynchronous, hence the waits.
            for (int pass = 0; pass < 2; pass++)
            {
                WindowPlacer.Place(a, placement.Main!.Value, wholeMonitor: true);
                WindowPlacer.Place(b, placement.Alt!.Value, wholeMonitor: monitors.Any(m => m.Bounds == placement.Alt));
                Thread.Sleep(400);
            }
            Check(IsPlaced(a, placement.Main.Value, monitors) && IsPlaced(b, placement.Alt.Value, monitors),
                $"{layout}: main {Describe(a)}, alt {Describe(b)} (want alt {placement.Alt})");
        }
        var received = File.ReadAllLines(logA).Concat(File.ReadAllLines(logB)).ToList();
        Check(received.Count == 0, "neither game received a single key or button message");
    }

    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);

    static bool IsPlaced(IntPtr hWnd, Rectangle target, IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Any(m => m.Bounds == target))
            return IsZoomed(hWnd) && Screen.FromHandle(hWnd).Bounds == target;
        return !IsZoomed(hWnd) && Native.GetVisibleBounds(hWnd, out var got) && got == target;
    }

    static string Describe(IntPtr hWnd)
    {
        Native.GetVisibleBounds(hWnd, out var got);
        return $"{got}{(IsZoomed(hWnd) ? " maximized" : "")} on {Screen.FromHandle(hWnd).Bounds}";
    }

    static void Focus(string logA, string logB)
    {
        Section("real focus swap between two fake games");
        var a = FindWindow(null, "Fake Game A");
        var b = FindWindow(null, "Fake Game B");
        if (a == IntPtr.Zero || b == IntPtr.Zero) { Check(false, "fake games are running"); return; }

        var found = WowWindowFinder.Find(new[] { "FakeGame" });
        Check(found.Count == 2 && found.All(w => w.ProcessName == "FakeGame") && found.Any(w => w.Handle == a) && found.Any(w => w.Handle == b),
            "both found by process name, from the process snapshot");

        IntPtr before = GetForegroundWindow();
        var toA = WindowActivator.BringToFront(a);
        Thread.Sleep(150);
        Check(toA != SwitchResult.Failed && GetForegroundWindow() == a, $"A brought to front ({toA})");
        var toB = WindowActivator.BringToFront(b);
        Thread.Sleep(150);
        Check(toB != SwitchResult.Failed && GetForegroundWindow() == b, $"B brought to front ({toB})");
        var backToA = WindowActivator.BringToFront(a);
        Thread.Sleep(150);
        Check(backToA != SwitchResult.Failed && GetForegroundWindow() == a, $"and back to A ({backToA})");
        if (before != IntPtr.Zero) WindowActivator.BringToFront(before);   // hand focus back to whatever you were doing

        Thread.Sleep(300);
        var received = File.ReadAllLines(logA).Concat(File.ReadAllLines(logB)).ToList();
        Check(received.Count == 0, "neither game received a single key or button message");
        foreach (var line in received) Console.WriteLine("       " + line);
    }
}
