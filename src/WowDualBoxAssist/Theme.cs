using Microsoft.Win32;

namespace WowDualBoxAssist;

/// <summary>Colour palette for the window, applied to every control at runtime so it can switch without a restart.</summary>
internal sealed record Theme(
    bool IsDark,
    Color Window,
    Color Text,
    Color MutedText,
    Color Border,
    Color Input,
    Color Button,
    Color ButtonHover,
    Color Accent,
    Color Capture,
    Color Alert)
{
    public static readonly Theme Light = new(
        IsDark: false,
        Window: Color.FromArgb(243, 243, 246),
        Text: Color.FromArgb(28, 28, 32),
        MutedText: Color.FromArgb(112, 112, 122),
        Border: Color.FromArgb(200, 200, 208),
        Input: Color.White,
        Button: Color.FromArgb(230, 230, 236),
        ButtonHover: Color.FromArgb(216, 216, 224),
        Accent: Color.FromArgb(178, 207, 255),
        Capture: Color.FromArgb(255, 226, 156),
        Alert: Color.Firebrick);

    public static readonly Theme Dark = new(
        IsDark: true,
        Window: Color.FromArgb(32, 33, 38),
        Text: Color.FromArgb(232, 232, 236),
        MutedText: Color.FromArgb(150, 152, 162),
        Border: Color.FromArgb(70, 72, 82),
        Input: Color.FromArgb(44, 46, 54),
        Button: Color.FromArgb(58, 60, 70),
        ButtonHover: Color.FromArgb(74, 76, 88),
        Accent: Color.FromArgb(47, 111, 216),
        Capture: Color.FromArgb(138, 104, 28),
        Alert: Color.FromArgb(255, 138, 128));

    public const string TagMuted = "muted";         // Label drawn in the muted text colour
    public const string TagSeparator = "separator"; // 1px Panel drawn in the border colour
    public const string TagHotkey = "hotkey";       // The big label that shows the current swap key

    /// <summary>Resolves a settings value ("System", "Light", "Dark") to a palette.</summary>
    public static Theme FromSetting(string setting) => setting switch
    {
        "Dark" => Dark,
        "Light" => Light,
        _ => SystemPrefersDark() ? Dark : Light,
    };

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Apply(Control root)
    {
        root.BackColor = Window;
        root.ForeColor = Text;
        foreach (Control child in root.Controls) ApplyTo(child);
    }

    private void ApplyTo(Control c)
    {
        switch (c)
        {
            case CheckBox or RadioButton:
                c.BackColor = Window;
                c.ForeColor = Text;
                break;

            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = Button;
                button.ForeColor = Text;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.MouseOverBackColor = ButtonHover;
                button.FlatAppearance.MouseDownBackColor = Accent;
                break;

            case ComboBox combo:
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = Input;
                combo.ForeColor = Text;
                break;

            case TextBox box:
                box.BorderStyle = BorderStyle.FixedSingle;
                box.BackColor = Input;
                box.ForeColor = Text;
                break;

            case Label { Tag: TagHotkey } hotkey:
                hotkey.BackColor = Input;
                hotkey.ForeColor = Text;
                break;

            case Label label:
                label.BackColor = Window;
                label.ForeColor = label.Tag is TagMuted ? MutedText : Text;
                break;

            case Panel { Tag: TagSeparator } separator:
                separator.BackColor = Border;
                break;

            default:
                c.BackColor = Window;
                c.ForeColor = Text;
                break;
        }

        foreach (Control child in c.Controls) ApplyTo(child);
    }
}
