namespace WowDualBoxAssist;

/// <summary>Where the two game windows go. The main always fills the main (primary) monitor.</summary>
public enum WindowLayout
{
    None,             // leave the windows wherever they are
    BothOnMain,       // both fill the main monitor, on top of each other
    AltFillsLeft,     // alt fills the nearest monitor to the left of the main one
    AltFillsRight,
    AltCornerLeft,    // alt takes a quarter of the left monitor, in the bottom corner nearest the main one
    AltCornerRight,
}

public sealed record MonitorInfo(Rectangle Bounds, Rectangle WorkArea, bool Primary);

/// <summary>Where each window should go; null means leave that one alone.</summary>
public readonly record struct Placement(Rectangle? Main, Rectangle? Alt);

public static class Layouts
{
    public static string Describe(WindowLayout layout, IReadOnlyList<MonitorInfo> monitors) => layout switch
    {
        WindowLayout.None => "Don't move the windows",
        WindowLayout.BothOnMain => "Both on the main monitor",
        WindowLayout.AltFillsLeft => $"Alt fills the left monitor{Size(Left(monitors))}",
        WindowLayout.AltFillsRight => $"Alt fills the right monitor{Size(Right(monitors))}",
        WindowLayout.AltCornerLeft => $"Alt in the bottom-right quarter of the left monitor{Size(Left(monitors))}",
        WindowLayout.AltCornerRight => $"Alt in the bottom-left quarter of the right monitor{Size(Right(monitors))}",
        _ => layout.ToString(),
    };

    private static string Size(MonitorInfo? m) => m is null ? "" : $" ({m.Bounds.Width}×{m.Bounds.Height})";

    /// <summary>The layouts the current monitors allow, in menu order.</summary>
    public static IReadOnlyList<WindowLayout> Available(IReadOnlyList<MonitorInfo> monitors)
    {
        var list = new List<WindowLayout> { WindowLayout.BothOnMain };
        if (Left(monitors) is not null) list.AddRange(new[] { WindowLayout.AltFillsLeft, WindowLayout.AltCornerLeft });
        if (Right(monitors) is not null) list.AddRange(new[] { WindowLayout.AltFillsRight, WindowLayout.AltCornerRight });
        list.Add(WindowLayout.None);
        return list;
    }

    public static Placement Compute(WindowLayout layout, IReadOnlyList<MonitorInfo> monitors)
    {
        var main = monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault();
        if (main is null || layout == WindowLayout.None) return default;

        // Game windows fill the whole monitor, like borderless fullscreen. The corner window stays
        // inside the work area so the taskbar never covers it.
        Rectangle? alt = layout switch
        {
            WindowLayout.BothOnMain => main.Bounds,
            WindowLayout.AltFillsLeft => Left(monitors)?.Bounds,
            WindowLayout.AltFillsRight => Right(monitors)?.Bounds,
            WindowLayout.AltCornerLeft => Left(monitors) is { } l ? Quarter(l.WorkArea, rightSide: true) : null,
            WindowLayout.AltCornerRight => Right(monitors) is { } r ? Quarter(r.WorkArea, rightSide: false) : null,
            _ => null,
        };
        return new Placement(main.Bounds, alt);
    }

    private static Rectangle Quarter(Rectangle area, bool rightSide)
    {
        int w = area.Width / 2, h = area.Height / 2;
        return new Rectangle(rightSide ? area.Right - w : area.Left, area.Bottom - h, w, h);
    }

    /// <summary>The nearest monitor whose middle is left of the main monitor's middle.</summary>
    public static MonitorInfo? Left(IReadOnlyList<MonitorInfo> monitors)
    {
        var main = monitors.FirstOrDefault(m => m.Primary);
        if (main is null) return null;
        return monitors.Where(m => !m.Primary && Middle(m) < Middle(main)).MaxBy(m => m.Bounds.Right);
    }

    public static MonitorInfo? Right(IReadOnlyList<MonitorInfo> monitors)
    {
        var main = monitors.FirstOrDefault(m => m.Primary);
        if (main is null) return null;
        return monitors.Where(m => !m.Primary && Middle(m) > Middle(main)).MinBy(m => m.Bounds.Left);
    }

    private static int Middle(MonitorInfo m) => m.Bounds.Left + m.Bounds.Width / 2;

    public static IReadOnlyList<MonitorInfo> CurrentMonitors() =>
        Screen.AllScreens.Select(s => new MonitorInfo(s.Bounds, s.WorkingArea, s.Primary)).ToList();
}

internal static class WindowPlacer
{
    /// <summary>
    /// Puts a window on a monitor or part of one, the way snapping or maximizing it by hand would.
    /// Sends it no input. A target that is a whole monitor is reached by maximizing on that monitor:
    /// WoW's "Windowed (Fullscreen)" mode is a maximized window, and a window that was merely resized
    /// to full size leaves WoW in plain windowed mode at the old resolution.
    /// </summary>
    public static void Place(IntPtr hWnd, Rectangle target, bool wholeMonitor)
    {
        if (!Native.IsWindow(hWnd)) return;

        // Already maximized on the right monitor and actually covering it: leave it completely alone.
        // The maximized flag alone is not enough, as a resized WoW window can keep it at a quarter size.
        if (wholeMonitor && Native.IsZoomed(hWnd) && Native.GetVisibleBounds(hWnd, out var now)
            && Screen.FromHandle(hWnd).Bounds == target && now.Width * 10 >= target.Width * 9 && now.Height * 10 >= target.Height * 9)
            return;

        // A maximized or minimized window ignores a new position until it is restored. Every step is
        // asynchronous (handled in order by the game's own thread), so a busy game never stalls this app.
        if (Native.IsZoomed(hWnd) || Native.IsIconic(hWnd)) Native.ShowWindowAsync(hWnd, Native.SW_SHOWNOACTIVATE);

        if (wholeMonitor)
        {
            // Put the restored window on the target monitor first, since a window maximizes on the
            // monitor it is on. Windows cannot maximize a window without bringing it to the front.
            SetBounds(hWnd, Rectangle.Inflate(target, -target.Width / 8, -target.Height / 8));
            Native.ShowWindowAsync(hWnd, Native.SW_MAXIMIZE);
            return;
        }
        SetBounds(hWnd, target);
    }

    private static void SetBounds(IntPtr hWnd, Rectangle target)
    {
        // Windows with a normal frame have invisible resize borders around them; grow the target by
        // those so the visible window lines up with the monitor edges. Borderless windows have none.
        if (Native.GetWindowRect(hWnd, out var outer) && Native.GetVisibleBounds(hWnd, out var visible))
        {
            target = Rectangle.FromLTRB(
                target.Left - (visible.Left - outer.Left),
                target.Top - (visible.Top - outer.Top),
                target.Right + (outer.Right - visible.Right),
                target.Bottom + (outer.Bottom - visible.Bottom));
        }

        Native.SetWindowPos(hWnd, IntPtr.Zero, target.X, target.Y, target.Width, target.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);
    }
}
