using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WowDualBoxAssist;

/// <summary>
/// Starts WoW through the Battle.net app, so every client logs in with Battle.net's own ticket.
/// Battle.net launches whichever game account its dropdown shows, so <see cref="BnetWindow"/> picks
/// the account in that dropdown first, and Battle.net's own log is read afterwards to confirm the
/// launch used it. Nothing here touches a game.
/// </summary>
internal static class BnetLauncher
{
    /// <summary>Battle.net.exe from its uninstall entry, else the default install folder. Null if neither exists.</summary>
    public static string? FindBattleNet()
    {
        const string uninstall = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Battle.net";
        using (var key = Registry.LocalMachine.OpenSubKey(uninstall))
        {
            if (key?.GetValue("InstallLocation") is string dir)
            {
                string exe = Path.Combine(dir, "Battle.net.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battle.net", "Battle.net.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>Starts the Battle.net app if it isn't running. It opens its window and logs in on its own.</summary>
    public static void EnsureRunning(string battleNetExe)
    {
        if (Native.ProcessNames().ContainsValue("Battle.net")) return;
        LaunchLog.Write("starting Battle.net");
        Process.Start(new ProcessStartInfo(battleNetExe) { UseShellExecute = false })?.Dispose();
    }

    // There is no command that only shows a game's page: "handle_uri battlenet://WoWF" does, but
    // Battle.net then hands "WoWF://" to the next launch, which WoW rejects. So the page is only checked.

    /// <summary>
    /// Asks Battle.net to launch <paramref name="product"/> (its launch code, e.g. WoW, WoWC, WoWF) with
    /// the account its dropdown shows, and returns the process id of the client that appears.
    /// </summary>
    public static Task<int> LaunchAsync(string battleNetExe, string product,
        IReadOnlyCollection<string> processNames, TimeSpan timeout) => Task.Run(() =>
    {
        var names = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        var before = GamePids(names);
        Exec(battleNetExe, $"launch {product}");

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            Thread.Sleep(200);
            int pid = GamePids(names).FirstOrDefault(p => !before.Contains(p));
            if (pid != 0)
            {
                LaunchLog.Write($"client started, pid {pid}");
                return pid;
            }
        }
        LaunchLog.Write("timed out waiting for the client");
        throw new TimeoutException($"Battle.net didn't start {product} within {timeout.TotalSeconds:0} seconds. " +
                                   "Check that the launch code is right and Battle.net is logged in.");
    });

    private static void Exec(string battleNetExe, string command)
    {
        LaunchLog.Write($"Battle.net --exec=\"{command}\"");
        Process.Start(new ProcessStartInfo(battleNetExe, $"--exec=\"{command}\"") { UseShellExecute = false })?.Dispose();
    }

    private static HashSet<int> GamePids(HashSet<string> names) =>
        Native.ProcessNames().Where(p => names.Contains(p.Value)).Select(p => p.Key).ToHashSet();

    // Lines from Battle.net's own log (times are UTC):
    // "I 2026-10-28 21:08:51.515942 [InstallManager] {Main} Game launch options: programId=WoW GAME_ACCOUNT=WoW2 REGION=US ..."
    // "D 2026-10-28 21:25:02.025407 [GameController] {Main} Active product set to: WoWFNoUpgrade_wowf"
    private static readonly Regex Stamp = new(@"^\S+ (?<time>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+) ", RegexOptions.Compiled);
    private static readonly Regex LaunchedAccount = new(@"Game launch options: .*\bGAME_ACCOUNT=(?<v>\S*)", RegexOptions.Compiled);
    private static readonly Regex ActiveProduct = new(@"\] \{Main\} Active product set to: (?<v>\S+)", RegexOptions.Compiled);

    /// <summary>
    /// The game account Battle.net says it launched with at or after <paramref name="sinceUtc"/>,
    /// or null if it hasn't logged a launch since then.
    /// </summary>
    public static string? LaunchedAccountSince(DateTime sinceUtc) => Latest(RecentLogLines(), LaunchedAccount, sinceUtc);

    internal static string? LaunchedAccountSince(IEnumerable<string> lines, DateTime sinceUtc) => Latest(lines, LaunchedAccount, sinceUtc);

    /// <summary>
    /// The product whose page Battle.net shows (e.g. "WoWFNoUpgrade_wowf"), or null if its log doesn't say.
    /// Logged whenever the page or its account changes.
    /// </summary>
    public static string? ShownProduct() => Latest(RecentLogLines(), ActiveProduct, DateTime.MinValue);

    internal static string? ShownProduct(IEnumerable<string> lines) => Latest(lines, ActiveProduct, DateTime.MinValue);

    /// <summary>
    /// Whether a product from <see cref="ShownProduct()"/> belongs to the launch code: "WoWFNoUpgrade_wowf"
    /// and "WoWFPreloadAndPlay_wowf" are WoWF, "WoW_retail" is WoW. "WoWFNoUpgrade" is not WoW: the
    /// launch code has to end where a new word starts.
    /// </summary>
    public static bool IsProductOf(string product, string launchCode)
    {
        string name = product.Split('_')[0];
        if (!name.StartsWith(launchCode, StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Length == launchCode.Length) return true;
        return name.Length > launchCode.Length + 1 && char.IsUpper(name[launchCode.Length]) && char.IsLower(name[launchCode.Length + 1]);
    }

    /// <summary>The value from the newest line matching <paramref name="pattern"/> at or after <paramref name="sinceUtc"/>.</summary>
    private static string? Latest(IEnumerable<string> lines, Regex pattern, DateTime sinceUtc)
    {
        string? value = null;
        var newest = DateTime.MinValue;
        foreach (string line in lines)
        {
            var m = pattern.Match(line);
            if (!m.Success) continue;
            var stamp = Stamp.Match(line);
            if (!stamp.Success ||
                !DateTime.TryParseExact(stamp.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time) ||
                time < sinceUtc || time < newest)
                continue;
            newest = time;
            value = m.Groups["v"].Value;
        }
        return value;
    }

    /// <summary>
    /// The lines of Battle.net's newest log files. The app writes one log per session, and every
    /// --exec also starts a short-lived copy that writes a small log of its own, so the newest file
    /// alone isn't enough.
    /// </summary>
    private static IEnumerable<string> RecentLogLines()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Battle.net", "Logs");
        FileInfo[] logs;
        try { logs = new DirectoryInfo(dir).GetFiles("battle.net-*.log"); }
        catch (IOException) { yield break; }

        foreach (var log in logs.OrderByDescending(f => f.LastWriteTimeUtc).Take(5))
        {
            string text;
            try
            {
                // Battle.net keeps its log open for writing.
                using var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (string line in text.Split('\n')) yield return line;
        }
    }
}

/// <summary>
/// %AppData%\WowDualBoxAssist\launch.log: what each launch saw and did, with UTC times so it lines
/// up with Battle.net's own log.
/// </summary>
internal static class LaunchLog
{
    private static readonly object Lock = new();

    public static string FilePath => Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "launch.log");

    public static void Write(string line)
    {
        try
        {
            lock (Lock) File.AppendAllText(FilePath, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { /* diagnostics only */ }
    }
}
