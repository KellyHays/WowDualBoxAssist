using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WowDualBoxAssist;

/// <summary>A line of text read off the screen, in window pixels.</summary>
internal sealed record OcrLine(string Text, Rectangle Bounds);

/// <summary>
/// Picks the game account in the Battle.net app's account dropdown, the only place Battle.net takes
/// it from. It reads the window's text with Windows' own OCR, so it finds the dropdown and the account
/// by what they say rather than at fixed spots, and clicks by posting mouse messages to the Battle.net
/// window: the real cursor doesn't move and Battle.net needn't be in front.
///
/// These are the only clicks this app ever makes, and they only ever go to Battle.net, never to a game.
/// </summary>
internal static class BnetWindow
{
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint PW_RENDERFULLCONTENT = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, Native.EnumWindowsProc proc, IntPtr lParam);

    /// <summary>
    /// Makes the dropdown show <paramref name="account"/>. Battle.net has to be on the game's page
    /// (<see cref="BnetLauncher.ShownProduct()"/>). Throws <see cref="LaunchException"/> with a message for
    /// the user when it can't.
    /// </summary>
    public static async Task SelectAccountAsync(string account)
    {
        IntPtr window = await WaitForWindowAsync(TimeSpan.FromSeconds(60));
        if (Native.IsIconic(window))
        {
            Native.ShowWindow(window, Native.SW_SHOWNOACTIVATE);   // a minimized window draws nothing to read
            await Task.Delay(800);
        }

        for (int attempt = 0; attempt < 6; attempt++)
        {
            var lines = await ReadAsync(window);
            var play = AccountPicker.PlayButton(lines);
            var box = play is null ? null : AccountPicker.AccountBox(lines, play);
            if (box is null)
            {
                await Task.Delay(1000);   // still loading the page
                continue;
            }
            if (AccountPicker.Matches(box.Text, account))
            {
                LaunchLog.Write($"account dropdown shows '{box.Text}'");
                return;
            }

            LaunchLog.Write($"account dropdown shows '{box.Text}', opening it");
            Click(window, Center(box.Bounds));
            await Task.Delay(700);

            var item = AccountPicker.AccountItem(await ReadAsync(window), box, account);
            if (item is null)
            {
                Click(window, Center(box.Bounds));   // close the list again
                throw new LaunchException($"Battle.net's account list has no \"{account}\". Use the name as the list shows it.");
            }
            LaunchLog.Write($"picking '{item.Text}'");
            Click(window, Center(item.Bounds));
            await Task.Delay(700);
        }
        throw new LaunchException($"Couldn't switch Battle.net's account dropdown to {account}. Is the game's page showing?");
    }

    /// <summary>Waits for Battle.net's main window, e.g. while Battle.net starts. Throws <see cref="LaunchException"/> on timeout.</summary>
    public static async Task<IntPtr> WaitForWindowAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            IntPtr window = Find();
            if (window != IntPtr.Zero) return window;
            if (DateTime.UtcNow > deadline) throw new LaunchException("Battle.net's window didn't open.");
            await Task.Delay(500);
        }
    }

    /// <summary>The Battle.net app's main window, or zero.</summary>
    private static IntPtr Find()
    {
        var processes = Native.ProcessNames();
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((hWnd, _) =>
        {
            if (!Native.IsWindowVisible(hWnd) || Native.GetWindowTitle(hWnd) != "Battle.net") return true;
            if (!Native.GetWindowClass(hWnd).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return true;
            Native.GetWindowThreadProcessId(hWnd, out uint pid);
            if (processes.GetValueOrDefault((int)pid) != "Battle.net") return true;
            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Every line of text in the window, positioned in window pixels.</summary>
    private static async Task<List<OcrLine>> ReadAsync(IntPtr window)
    {
        using var image = Capture(window);
        return await Ocr(image);
    }

    private static Bitmap Capture(IntPtr window)
    {
        GetWindowRect(window, out var r);
        var image = new Bitmap(Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(image);
        IntPtr hdc = g.GetHdc();
        try { PrintWindow(window, hdc, PW_RENDERFULLCONTENT); }   // works behind other windows, unlike a screen grab
        finally { g.ReleaseHdc(hdc); }
        return image;
    }

    private static async Task<List<OcrLine>> Ocr(Bitmap image)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? throw new LaunchException("Windows text recognition isn't available.");

        // Large windows are shrunk to what the engine accepts; the positions are scaled back up.
        double scale = Math.Min(1.0, (double)OcrEngine.MaxImageDimension / Math.Max(image.Width, image.Height));
        using var input = scale < 1.0 ? new Bitmap(image, (int)(image.Width * scale), (int)(image.Height * scale)) : new Bitmap(image);

        var data = input.LockBits(new Rectangle(Point.Empty, input.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels = new byte[data.Stride * data.Height];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        input.UnlockBits(data);

        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, input.Width, input.Height, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(pixels.AsBuffer());
        var result = await engine.RecognizeAsync(bitmap);

        return result.Lines.Select(line =>
        {
            var words = line.Words.Select(w => w.BoundingRect).ToList();
            double left = words.Min(w => w.X), top = words.Min(w => w.Y);
            double right = words.Max(w => w.X + w.Width), bottom = words.Max(w => w.Y + w.Height);
            return new OcrLine(line.Text, Rectangle.FromLTRB(
                (int)(left / scale), (int)(top / scale), (int)(right / scale), (int)(bottom / scale)));
        }).ToList();
    }

    /// <summary>A left click at a window pixel, posted to the page inside the window.</summary>
    private static void Click(IntPtr window, Point windowPixel)
    {
        GetWindowRect(window, out var r);
        IntPtr target = RenderWidget(window);
        var p = new POINT { X = r.Left + windowPixel.X, Y = r.Top + windowPixel.Y };
        ScreenToClient(target, ref p);
        var lParam = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));

        PostMessage(target, WM_MOUSEMOVE, IntPtr.Zero, lParam);
        Thread.Sleep(30);
        PostMessage(target, WM_LBUTTONDOWN, (IntPtr)1, lParam);   // MK_LBUTTON
        Thread.Sleep(30);
        PostMessage(target, WM_LBUTTONUP, IntPtr.Zero, lParam);
    }

    /// <summary>The child window Battle.net's page is drawn in, which takes the mouse; the main window if there is none.</summary>
    private static IntPtr RenderWidget(IntPtr window)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(window, (child, _) =>
        {
            if (Native.GetWindowClass(child) != "Chrome_RenderWidgetHostHWND") return true;
            found = child;
            return false;
        }, IntPtr.Zero);
        return found != IntPtr.Zero ? found : window;
    }

    private static Point Center(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);
}

/// <summary>Finds the account dropdown and its entries among the lines read off Battle.net's window.</summary>
internal static class AccountPicker
{
    /// <summary>
    /// Upper case without spaces, with the letters OCR mixes up with digits turned into the digits:
    /// "wowi (US)" and "WoW1 (US)" both become "W0W1(US)".
    /// </summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.ToUpperInvariant())
        {
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(c switch { 'O' => '0', 'I' or 'L' or '|' or '!' => '1', _ => c });
        }
        return sb.ToString();
    }

    /// <summary>
    /// Whether a line names the account: "WoW1" matches "WoW1 (US)" and "WoW1 (EU) - Starter Edition",
    /// but not "WoW12 (US)". "WoW1 (US)" matches only that one.
    /// </summary>
    public static bool Matches(string lineText, string account)
    {
        string line = Normalize(lineText), name = Normalize(account);
        if (name.Length == 0 || !line.StartsWith(name, StringComparison.Ordinal)) return false;
        return line.Length == name.Length || line[name.Length] is '(' or '-' || name.EndsWith(')');
    }

    /// <summary>The Play button: a line reading just "Play", leftmost if there are several.</summary>
    public static OcrLine? PlayButton(IEnumerable<OcrLine> lines) =>
        lines.Where(l => Normalize(l.Text) == Normalize("Play")).MinBy(l => l.Bounds.Left);

    /// <summary>The account dropdown: the nearest line above the Play button that starts at or left of it.</summary>
    public static OcrLine? AccountBox(IEnumerable<OcrLine> lines, OcrLine play) =>
        lines.Where(l => l != play
                         && l.Bounds.Bottom <= play.Bounds.Top
                         && l.Bounds.Left <= play.Bounds.Left
                         && play.Bounds.Top - l.Bounds.Bottom <= 4 * play.Bounds.Height)
             .MaxBy(l => l.Bounds.Bottom);

    /// <summary>
    /// The account's entry in the open list above the dropdown. A full account wins over a Starter
    /// Edition one with the same name, then the entry nearest the dropdown.
    /// </summary>
    public static OcrLine? AccountItem(IEnumerable<OcrLine> lines, OcrLine box, string account) =>
        lines.Where(l => l.Bounds.Bottom <= box.Bounds.Top && Matches(l.Text, account))
             .OrderBy(l => Normalize(l.Text).Contains("STARTER", StringComparison.Ordinal))
             .ThenBy(l => box.Bounds.Top - l.Bounds.Bottom)
             .FirstOrDefault();
}

/// <summary>A launch step that failed, with a message meant for the user.</summary>
internal sealed class LaunchException(string message) : Exception(message);
