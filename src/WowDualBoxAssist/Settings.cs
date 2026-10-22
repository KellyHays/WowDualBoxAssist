using System.Text.Json;

namespace WowDualBoxAssist;

/// <summary>User settings, persisted as JSON under %AppData%\WowDualBoxAssist.</summary>
public sealed class Settings
{
    /// <summary>The key or mouse button that swaps between the two games. Unset until the user picks one.</summary>
    public Hotkey SwapHotkey { get; set; }

    /// <summary>Whether the swap key also works from other windows (brings back the game used last).</summary>
    public bool SwapFromAnyWindow { get; set; } = true;

    /// <summary>Where the game windows are put when they are found, when the layout is picked, and on Apply.</summary>
    public WindowLayout Layout { get; set; } = WindowLayout.BothOnMain;

    public string ProcessNames { get; set; } = "Wow, Wow-64, WowClassic, WowT, WowB";

    /// <summary>"System" follows the Windows app theme; "Light" or "Dark" forces one.</summary>
    public string Theme { get; set; } = "System";

    /// <summary>
    /// Swaps made, and how many of them only worked via the input-sharing fallback. Kept across
    /// sessions to decide whether the fallback is needed at all.
    /// </summary>
    public long SwapCount { get; set; }
    public long FallbackCount { get; set; }

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WowDualBoxAssist", "settings.json");

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
        }
        catch { /* corrupt or unreadable file: fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { /* settings are a convenience; never crash over them */ }
    }
}
