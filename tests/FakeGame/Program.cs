// Stand-in for a game client: a bare window that logs every keyboard and mouse-button message it
// receives, so the focus test can check that swapping delivers nothing to the games.
// Usage: FakeGame "<window title>" <log file> <x position>
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new LoggingForm(args[0], args[1], int.Parse(args[2])));
    }
}

sealed class LoggingForm : Form
{
    private readonly string _log;

    public LoggingForm(string title, string log, int x)
    {
        _log = log;
        Text = title;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(x, 60);
        Size = new Size(420, 260);
        File.WriteAllText(_log, "");
        var label = new Label { Dock = DockStyle.Fill, Text = title + "\nhwnd=" + Handle.ToInt64(), Font = new Font("Segoe UI", 14F) };
        Controls.Add(label);
        label.Enabled = false; // keep keyboard focus on the form itself
    }

    // Opens in the background, so starting the fake games never takes focus from what you are doing.
    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        string? name = m.Msg switch
        {
            0x0100 => "WM_KEYDOWN",
            0x0101 => "WM_KEYUP",
            0x0102 => "WM_CHAR",
            0x0104 => "WM_SYSKEYDOWN",
            0x0105 => "WM_SYSKEYUP",
            0x0207 => "WM_MBUTTONDOWN",
            0x020B => "WM_XBUTTONDOWN",
            _ => null,
        };
        if (name is not null)
        {
            uint lp = (uint)(m.LParam.ToInt64() & 0xFFFFFFFF);
            File.AppendAllText(_log, $"{name} wp=0x{m.WParam.ToInt64():X2} lp=0x{lp:X8}{Environment.NewLine}");
        }
        base.WndProc(ref m);
    }
}
