using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Reach.Agent.Tray;

/// <summary>
/// The Reach mark (a cursor with two signal arcs, as in the app's icon), drawn at startup:
/// gray while idle, electric blue while a phone is connected (spec §5).
/// </summary>
public static class TrayIcons
{
    public static readonly Color Accent = Color.FromArgb(0x4F, 0x7C, 0xFF);

    public static Icon Idle { get; } = ToIcon(Draw(Color.FromArgb(0x6B, 0x72, 0x80), Color.FromArgb(0x4B, 0x52, 0x60), 32));
    public static Icon Active { get; } = ToIcon(Draw(Color.FromArgb(0x6D, 0x93, 0xFF), Color.FromArgb(0x3A, 0x5B, 0xF0), 32));

    /// <summary>The mark on a rounded square, in a 100×100 design space scaled to <paramref name="size"/> pixels.</summary>
    public static Bitmap Draw(Color from, Color to, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.ScaleTransform(size / 100f, size / 100f);

        using (var background = RoundedSquare(100, 22))
        using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(100, 100), from, to))
            g.FillPath(fill, background);

        PointF[] arrow = [new(30, 30), new(30, 76), new(41, 65), new(48.5f, 81), new(56, 77.5f), new(48.5f, 62), new(63, 62)];
        using (var white = new SolidBrush(Color.White))
        using (var outline = new Pen(Color.White, 3) { LineJoin = LineJoin.Round })
        {
            g.FillPolygon(white, arrow);
            g.DrawPolygon(outline, arrow);
        }

        // The same arcs as the app icon's SVG, as centre, radius and angles (clockwise from +x, y down).
        using var arcs = new Pen(Color.White, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arcs, 44.66f - 22, 47.34f - 22, 44, 44, -75.9f, 61.8f);
        g.DrawArc(arcs, 45.17f - 34, 46.83f - 34, 68, 68, -74.9f, 59.8f);
        return bitmap;
    }

    private static GraphicsPath RoundedSquare(float size, float r)
    {
        var path = new GraphicsPath();
        path.AddArc(0, 0, 2 * r, 2 * r, 180, 90);
        path.AddArc(size - 2 * r, 0, 2 * r, 2 * r, 270, 90);
        path.AddArc(size - 2 * r, size - 2 * r, 2 * r, 2 * r, 0, 90);
        path.AddArc(0, size - 2 * r, 2 * r, 2 * r, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
        using (bitmap)
        {
            var handle = bitmap.GetHicon();
            try
            {
                return (Icon)Icon.FromHandle(handle).Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}
