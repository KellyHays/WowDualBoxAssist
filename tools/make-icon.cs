// Draws the app icon: two overlapping game windows (blue main, gold alt) with swap arrows, on a
// dark rounded tile. Writes a multi-size .ico, plus a preview strip of every size.
// Usage: dotnet run tools/make-icon.cs -- <out.ico> [preview.png]
#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property PublishAot=false
#:property PublishTrimmed=false

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var images = sizes.Select(Draw).ToList();
WriteIco(args[0], images);
if (args.Length > 1) WritePreview(args[1], images);
Console.WriteLine($"wrote {args[0]} ({string.Join(", ", sizes)})");

static Bitmap Draw(int s)
{
    var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);
    bool tiny = s <= 32;       // no arrows or title bars: they turn to mush at these sizes
    float u = s;               // everything below is in fractions of the icon size

    // Tile
    var tile = new RectangleF(0.02f * u, 0.02f * u, 0.96f * u, 0.96f * u);
    using (var path = Rounded(tile, 0.22f * u))
    using (var fill = new LinearGradientBrush(tile, Color.FromArgb(44, 50, 68), Color.FromArgb(22, 25, 34), 90f))
        g.FillPath(fill, path);

    // Back window (main, blue) top-left, front window (alt, gold) bottom-right
    var back = tiny ? Frac(0.10f, 0.12f, 0.56f, 0.50f, u) : Frac(0.12f, 0.14f, 0.50f, 0.40f, u);
    var front = tiny ? Frac(0.36f, 0.40f, 0.56f, 0.50f, u) : Frac(0.38f, 0.46f, 0.50f, 0.40f, u);
    DrawWindow(g, back, Color.FromArgb(88, 150, 255), Color.FromArgb(52, 104, 214), u, tiny, outline: false);
    DrawWindow(g, front, Color.FromArgb(246, 190, 66), Color.FromArgb(206, 140, 30), u, tiny, outline: true);

    if (!tiny)
    {
        // Swap arrows in the two empty corners. Each leaves the edge of one window and points into the
        // other: main -> alt down the right, alt -> main up the left. Mirror images of each other.
        float w = (s <= 64 ? 0.072f : 0.058f) * u;   // bolder when small so the arrows stay readable
        Arrow(g, cx: 0.62f * u, cy: 0.46f * u, r: 0.20f * u, start: -90, sweep: 72, w, u);
        Arrow(g, cx: 0.38f * u, cy: 0.54f * u, r: 0.20f * u, start: 90, sweep: 72, w, u);
    }
    return bmp;
}

static RectangleF Frac(float x, float y, float w, float h, float u) => new(x * u, y * u, w * u, h * u);

static void DrawWindow(Graphics g, RectangleF r, Color body, Color bar, float u, bool tiny, bool outline)
{
    float radius = Math.Max(1.5f, 0.07f * u);
    using var path = Rounded(r, radius);
    if (outline)
    {
        // A ring in the tile colour separates the front window from the one behind it.
        using var ring = new Pen(Color.FromArgb(26, 30, 41), Math.Max(1.5f, 0.045f * u)) { LineJoin = LineJoin.Round };
        g.DrawPath(ring, path);
    }
    using (var fill = new LinearGradientBrush(r, ControlPaint.Light(body, 0.15f), body, 90f))
        g.FillPath(fill, path);
    if (tiny) return;

    // Title bar strip with a "screen" inside, so it reads as a window rather than a block
    var state = g.Save();
    g.SetClip(path);
    using (var b = new SolidBrush(bar)) g.FillRectangle(b, r.X, r.Y, r.Width, 0.085f * u);
    g.Restore(state);
    var screen = RectangleF.Inflate(new RectangleF(r.X, r.Y + 0.085f * u, r.Width, r.Height - 0.085f * u), -0.04f * u, -0.04f * u);
    using (var screenPath = Rounded(screen, Math.Max(1f, 0.03f * u)))
    using (var shade = new SolidBrush(Color.FromArgb(46, 0, 0, 0)))
        g.FillPath(shade, screenPath);
}

/// <summary>
/// A curved arrow along a clockwise arc, built as one shape (shaft + head) so the head continues the
/// curve without a gap or kink, then drawn with a soft shadow, a dark outline and a light gradient.
/// </summary>
static void Arrow(Graphics g, float cx, float cy, float r, float start, float sweep, float width, float u)
{
    using var shape = new GraphicsPath(FillMode.Winding);

    // Shaft: the arc thickened into an outline, round at the tail and square where the head attaches.
    using (var shaft = new GraphicsPath())
    using (var pen = new Pen(Color.Black, width) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
    {
        shaft.AddArc(cx - r, cy - r, 2 * r, 2 * r, start, sweep);
        shaft.Widen(pen);
        shape.AddPath(shaft, false);
    }

    // Head: its base sits on the end of the shaft (tucked back a little so no seam shows) and it
    // points along the curve's direction at that spot.
    double end = (start + sweep) * Math.PI / 180;
    var at = new PointF(cx + r * (float)Math.Cos(end), cy + r * (float)Math.Sin(end));
    var dir = new PointF(-(float)Math.Sin(end), (float)Math.Cos(end));   // clockwise tangent
    var side = new PointF(-dir.Y, dir.X);
    float length = width * 1.75f, half = width * 1.25f, tuck = width * 0.25f;
    PointF P(float along, float across) => new(at.X + dir.X * along + side.X * across, at.Y + dir.Y * along + side.Y * across);
    shape.AddPolygon(new[] { P(length, 0), P(-tuck, half), P(-tuck, -half) });

    // Soft drop shadow, then a dark outline so the arrow stays clear where it crosses a window.
    using (var shadow = (GraphicsPath)shape.Clone())
    using (var offset = new System.Drawing.Drawing2D.Matrix())
    using (var shadowBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
    {
        offset.Translate(0.012f * u, 0.016f * u);
        shadow.Transform(offset);
        g.FillPath(shadowBrush, shadow);
    }
    using (var outline = new Pen(Color.FromArgb(22, 25, 34), Math.Max(1f, 0.03f * u)) { LineJoin = LineJoin.Round })
        g.DrawPath(outline, shape);
    var bounds = shape.GetBounds();
    using var fill = new LinearGradientBrush(RectangleF.Inflate(bounds, 1, 1), Color.White, Color.FromArgb(214, 222, 236), 90f);
    g.FillPath(fill, shape);
}

static GraphicsPath Rounded(RectangleF r, float radius)
{
    float d = radius * 2;
    var p = new GraphicsPath();
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

// ICO: 256 as PNG, the rest as 32-bit DIBs (the most widely understood form).
static void WriteIco(string path, List<Bitmap> images)
{
    var blobs = images.Select(b => b.Width >= 256 ? Png(b) : Dib(b)).ToList();
    using var w = new BinaryWriter(File.Create(path));
    w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
    int offset = 6 + 16 * images.Count;
    for (int i = 0; i < images.Count; i++)
    {
        int s = images[i].Width;
        w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
        w.Write(blobs[i].Length); w.Write(offset);
        offset += blobs[i].Length;
    }
    foreach (var blob in blobs) w.Write(blob);
}

static byte[] Png(Bitmap b)
{
    using var ms = new MemoryStream();
    b.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static byte[] Dib(Bitmap b)
{
    int s = b.Width;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
    w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    for (int y = s - 1; y >= 0; y--)          // bottom-up BGRA
        for (int x = 0; x < s; x++)
        {
            var c = b.GetPixel(x, y);
            w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
        }
    int maskStride = (s + 31) / 32 * 4;        // AND mask: all zero, alpha does the work
    w.Write(new byte[maskStride * s]);
    return ms.ToArray();
}

static void WritePreview(string path, List<Bitmap> images)
{
    int pad = 16, width = images.Sum(i => i.Width + pad) + pad, height = 256 + 2 * pad;
    using var strip = new Bitmap(width * 2, height);
    using var g = Graphics.FromImage(strip);
    g.Clear(Color.FromArgb(240, 240, 244));
    g.FillRectangle(new SolidBrush(Color.FromArgb(32, 33, 38)), width, 0, width, height);
    for (int half = 0; half < 2; half++)       // light and dark backgrounds
    {
        int x = half * width + pad;
        foreach (var img in images) { g.DrawImageUnscaled(img, x, pad + 256 - img.Height); x += img.Width + pad; }
    }
    strip.Save(path, ImageFormat.Png);
}
