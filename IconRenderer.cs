using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Toasty;

/// <summary>Draws the little number icons that sit in the tray.</summary>
public static class IconRenderer
{
    public static int Size => Math.Max(16, SystemInformation.SmallIconSize.Width);

    // Segoe UI Bold: a closed "4" and clean digits that hold up at 9-14px.
    private static readonly Lazy<FontFamily> DigitFont = new(() =>
        FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe UI") ?? FontFamily.GenericSansSerif);
    private const FontStyle DigitStyle = FontStyle.Bold;

    private static readonly Lazy<RectangleF> ReferenceBounds = new(() =>
    {
        using var p = new GraphicsPath();
        p.AddString("88", DigitFont.Value, (int)DigitStyle, 100f, PointF.Empty, StringFormat.GenericTypographic);
        return p.GetBounds();
    });

    /// <summary>True when the taskbar uses the light theme (labels and outlines then go dark).</summary>
    public static bool LightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Draws one metric in the given style and returns it as a tray icon.</summary>
    public static Icon Render(IconStyle style, double? value, double fraction, Color color, string tag,
        bool solidBackground, bool lightTaskbar)
    {
        using var bmp = RenderBitmap(style, value, fraction, color, tag, solidBackground, lightTaskbar, Size);
        return ToIcon(bmp);
    }

    /// <summary>
    /// Draws one metric at <paramref name="size"/> px. A null value draws a grey dash / empty bar.
    /// <paramref name="fraction"/> (0..1) is how full bars and meters are.
    /// </summary>
    public static Bitmap RenderBitmap(IconStyle style, double? value, double fraction, Color color, string tag,
        bool solidBackground, bool lightTaskbar, int size)
    {
        // Solid background is always dark, so treat it like a dark taskbar.
        bool darkInk = lightTaskbar && !solidBackground;
        int scale = Math.Max(1, size / 16);
        var labelColor = darkInk ? Color.FromArgb(63, 63, 70) : Color.FromArgb(212, 212, 216);
        var trackColor = darkInk ? Color.FromArgb(55, 0, 0, 0) : Color.FromArgb(60, 255, 255, 255);
        if (!value.HasValue)
        {
            color = Color.Gray;
            fraction = 0;
        }
        string text = value.HasValue ? Math.Round(value.Value).ToString("0") : "-";
        int labelBottom = PixelFont.Height * scale + scale; // label rows plus a one-pixel gap

        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        if (solidBackground)
        {
            using var bg = new SolidBrush(Color.FromArgb(235, 24, 24, 27));
            using var path = RoundedRect(new RectangleF(0, 0, size, size), size / 5f);
            g.FillPath(bg, path);
        }

        void Label()
        {
            g.Flush(); // the label is set pixel by pixel; make sure the background is down first
            PixelFont.Draw(bmp, tag, labelColor, scale, y: 0);
        }

        switch (style)
        {
            case IconStyle.NumberOnly:
                DrawDigits(g, text, color, size, top: 1, bottom: 1, outline: darkInk);
                break;

            case IconStyle.NumberAndBar:
            {
                int barH = 2 * scale;
                DrawDigits(g, text, color, size, top: 0, bottom: barH + scale, outline: darkInk);
                HorizontalBar(g, new Rectangle(1, size - barH, size - 2, barH), fraction, color, trackColor, frame: false);
                break;
            }

            case IconStyle.LabelAndBar:
                Label();
                HorizontalBar(g, new Rectangle(1, labelBottom + scale, size - 2, size - labelBottom - 2 * scale),
                    fraction, color, trackColor, frame: true);
                break;

            case IconStyle.Meter:
            {
                int w = Math.Max(6, size / 2);
                var r = new Rectangle((size - w) / 2, 0, w, size);
                VerticalBar(g, r, fraction, color, trackColor);
                break;
            }

            case IconStyle.Dot:
            {
                float d = size - 4f * scale;
                var r = new RectangleF((size - d) / 2f, (size - d) / 2f, d, d);
                if (value.HasValue)
                {
                    using var b = new SolidBrush(color);
                    g.FillEllipse(b, r);
                    if (darkInk)
                    {
                        using var pen = new Pen(Color.FromArgb(150, 0, 0, 0), 1f);
                        g.DrawEllipse(pen, r);
                    }
                }
                else
                {
                    using var pen = new Pen(color, 1.5f * scale);
                    g.DrawEllipse(pen, r);
                }
                break;
            }

            default: // LabelAndNumber
                Label();
                DrawDigits(g, text, color, size, top: labelBottom, bottom: 1, outline: darkInk);
                break;
        }

        return bmp;
    }

    /// <summary>A left-to-right level bar on whole pixels so its edges stay crisp.</summary>
    private static void HorizontalBar(Graphics g, Rectangle r, double fraction, Color color, Color track, bool frame)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (var t = new SolidBrush(track)) g.FillRectangle(t, r);
        var inner = frame ? Rectangle.Inflate(r, -1, -1) : r;
        int fill = (int)Math.Round(inner.Width * fraction);
        if (fill > 0)
        {
            using var b = new SolidBrush(color);
            g.FillRectangle(b, inner.X, inner.Y, fill, inner.Height);
        }
        g.SmoothingMode = old;
    }

    /// <summary>A bottom-up gauge with a one-pixel frame in the band colour.</summary>
    private static void VerticalBar(Graphics g, Rectangle r, double fraction, Color color, Color track)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using (var t = new SolidBrush(track)) g.FillRectangle(t, r);
        using (var pen = new Pen(color)) g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        var inner = Rectangle.Inflate(r, -2, -2);
        int fill = (int)Math.Round(inner.Height * fraction);
        if (fill > 0)
        {
            using var b = new SolidBrush(color);
            g.FillRectangle(b, inner.X, inner.Bottom - fill, inner.Width, fill);
        }
        g.SmoothingMode = old;
    }
    /// <summary>The Toasty logo, used for windows and as the fallback tray icon.</summary>
    public static Icon Logo(int? size = null)
    {
        using var stream = typeof(IconRenderer).Assembly.GetManifestResourceStream("Toasty.toasty.ico")!;
        int s = size ?? Size;
        return new Icon(stream, s, s);
    }

    private static void DrawDigits(Graphics g, string text, Color color, int size, int top, int bottom, bool outline)
    {
        // Lay the glyphs out from a point (a layout rectangle silently drops text that doesn't fit),
        // then scale the outline into the space below the label. Scale comes from a two-digit
        // reference ("88") so every icon has the same digit height whether it shows "4", "42" or
        // "-"; only a third digit ("100") squeezes narrower. Top and bottom land on whole pixels
        // so horizontal edges stay sharp.
        var family = DigitFont.Value;
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)DigitStyle, 100f, PointF.Empty, StringFormat.GenericTypographic);
        var bounds = path.GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var reference = ReferenceBounds.Value;
        int digitHeight = size - top - bottom;
        float availWidth = size - 1;
        float sy = digitHeight / reference.Height;
        float sx = Math.Min(sy * 1.1f, availWidth / Math.Max(reference.Width, bounds.Width));

        using (var m = new Matrix())
        {
            m.Translate(size / 2f, top);
            m.Scale(sx, sy);
            m.Translate(-(bounds.X + bounds.Width / 2f), -reference.Y);
            path.Transform(m);
        }

        if (outline)
        {
            // On a light taskbar, a thin dark outline keeps cyan/yellow readable.
            using var pen = new Pen(Color.FromArgb(170, 0, 0, 0), 1.5f) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
        }
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
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

    private static Icon ToIcon(Bitmap bmp)
    {
        // Icon.FromHandle doesn't own the HICON, so clone it and free the original handle straight away.
        IntPtr h = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(h);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>
/// A hand-drawn 5px-tall pixel font for the icon tags. Real fonts turn to mush at this size;
/// these glyphs are placed pixel by pixel so "CPU" stays legible in a 16px icon.
/// </summary>
internal static class PixelFont
{
    public const int Height = 5;

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['C'] = ["###", "#..", "#..", "#..", "###"],
        ['P'] = ["###", "#.#", "###", "#..", "#.."],
        ['U'] = ["#.#", "#.#", "#.#", "#.#", "###"],
        ['G'] = ["###", "#..", "#.#", "#.#", "###"],
        ['R'] = ["##.", "#.#", "##.", "#.#", "#.#"],
        ['A'] = ["###", "#.#", "###", "#.#", "#.#"],
        ['M'] = ["#...#", "##.##", "#.#.#", "#...#", "#...#"],
        ['H'] = ["#.#", "#.#", "###", "#.#", "#.#"],
        ['O'] = ["###", "#.#", "#.#", "#.#", "###"],
        ['T'] = ["###", ".#.", ".#.", ".#.", ".#."],
        ['E'] = ["###", "#..", "##.", "#..", "###"],
        ['°'] = ["##", "##", "..", "..", ".."],
        ['%'] = ["#.#", "..#", ".#.", "#..", "#.#"],
    };

    public static int Width(string text) =>
        text.Sum(c => Glyphs.TryGetValue(c, out var g) ? g[0].Length : 0) + Math.Max(0, text.Length - 1);

    public static void Draw(Bitmap bmp, string text, Color color, int scale, int y)
    {
        int x = (bmp.Width - Width(text) * scale) / 2;
        foreach (char c in text)
        {
            if (!Glyphs.TryGetValue(c, out var glyph)) continue;
            for (int row = 0; row < Height; row++)
            {
                for (int col = 0; col < glyph[row].Length; col++)
                {
                    if (glyph[row][col] != '#') continue;
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                        {
                            int px = x + col * scale + dx, py = y + row * scale + dy;
                            if (px >= 0 && px < bmp.Width && py >= 0 && py < bmp.Height) bmp.SetPixel(px, py, color);
                        }
                }
            }
            x += (glyph[0].Length + 1) * scale;
        }
    }
}
