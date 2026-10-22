namespace WowDualBoxAssist;

/// <summary>A top-level game window.</summary>
public sealed record WowWindow(IntPtr Handle, int Pid, string Title, string ClassName, string ProcessName);

/// <summary>
/// Finds the game windows from window titles, window classes and the system process list only.
/// No handle is ever opened on a game process.
/// </summary>
internal static class WowWindowFinder
{
    /// <summary>WoW's own top-level window class (GxWindowClass, GxWindowClassD3d, ...).</summary>
    private const string WowWindowClassPrefix = "GxWindowClass";

    public static IReadOnlyList<string> ParseProcessNames(string spec) =>
        spec.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Normalize(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name;
    }

    // Process id -> exe name. Taking the process snapshot costs several milliseconds on the thread
    // that also handles the swap key, so it is only retaken when a window from an unknown process shows up.
    private static readonly Dictionary<int, string> ProcessNameCache = new();

    private static string ProcessName(int pid)
    {
        if (ProcessNameCache.TryGetValue(pid, out var name)) return name;

        var snapshot = Native.ProcessNames();
        ProcessNameCache.Clear();
        foreach (var (id, exe) in snapshot) ProcessNameCache[id] = exe;
        // A process that is already gone gets an empty name, so it does not trigger another snapshot.
        // A reused id can keep an old name; WoW itself is still found by its window class.
        return ProcessNameCache.TryAdd(pid, "") ? "" : ProcessNameCache[pid];
    }

    /// <summary>
    /// Finds every visible top-level window that belongs to one of the given process names
    /// or uses WoW's own window class, in z-order. Our own process is always skipped.
    /// </summary>
    public static List<WowWindow> Find(IReadOnlyCollection<string> processNames)
    {
        var names = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        var result = new List<WowWindow>();
        int ownPid = Environment.ProcessId;

        Native.EnumWindows((hWnd, _) =>
        {
            if (!Native.IsWindowVisible(hWnd)) return true;

            Native.GetWindowThreadProcessId(hWnd, out uint pidRaw);
            int pid = (int)pidRaw;
            if (pid == ownPid) return true;

            string className = Native.GetWindowClass(hWnd);
            string processName = ProcessName(pid);

            bool matches = className.StartsWith(WowWindowClassPrefix, StringComparison.Ordinal)
                           || names.Contains(processName);
            if (!matches) return true;

            string title = Native.GetWindowTitle(hWnd);
            if (title.Length == 0) return true;   // helper/hidden windows never carry a title

            result.Add(new WowWindow(hWnd, pid, title, className, processName));
            return true;
        }, IntPtr.Zero);

        return result;
    }
}
