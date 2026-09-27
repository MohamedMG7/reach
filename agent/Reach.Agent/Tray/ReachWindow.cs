using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Reach.Agent.Tray;

/// <summary>
/// A Reach window: dark, with a dark title bar, the centered mark and the privacy line. Content is
/// painted in a fixed design space (<see cref="DesignSize"/>) scaled to the window's DPI.
/// </summary>
public abstract class ReachWindow : Form
{
    private readonly Image _logo = TrayIcons.Draw(Color.FromArgb(0x6D, 0x93, 0xFF), Color.FromArgb(0x3A, 0x5B, 0xF0), 128);

    protected ReachWindow(string windowTitle, Size designSize)
    {
        DesignSize = designSize;
        Text = windowTitle;
        Icon = TrayIcons.Active;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = TrayTheme.Background;
        DoubleBuffered = true;
        ClientSize = new Size(Px(designSize.Width), Px(designSize.Height));
    }

    protected Size DesignSize { get; }

    private float Factor => DeviceDpi / 96f;
    private int Px(int value) => (int)Math.Round(value * Factor);

    /// <summary>A point in the window, in design-space units.</summary>
    protected PointF ToDesign(Point p) => new(p.X / Factor, p.Y / Factor);

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ClientSize = new Size(Px(DesignSize.Width), Px(DesignSize.Height));
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // A dark title bar to match the window (Windows 10 20H1 and later; ignored elsewhere).
        int dark = 1;
        _ = DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    protected sealed override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.ScaleTransform(Factor, Factor);

        // One centered column on the plain background: the mark, the heading, the content, the promise.
        g.DrawImage(_logo, (DesignSize.Width - 36) / 2f, 28, 36, 36);
        if (Heading is { } heading)
        {
            using var title = DesignFont(20, FontStyle.Bold);
            using var text = new SolidBrush(TrayTheme.Text);
            g.DrawString(heading, title, text, new RectangleF(0, 74, DesignSize.Width, 30), Centered);
        }

        PaintContent(g);
        PaintFooter(g);
    }

    /// <summary>Shown under the mark; none when the content speaks for itself.</summary>
    protected virtual string? Heading => null;

    /// <summary>Paints between the header (ends at y 104) and the footer (starts at <see cref="DesignSize"/> height − 72).</summary>
    protected abstract void PaintContent(Graphics g);

    private void PaintFooter(Graphics g)
    {
        var top = DesignSize.Height - 72;
        using var bold = DesignFont(12.5f, FontStyle.Bold);
        using var small = DesignFont(11.5f);
        using var muted = new SolidBrush(TrayTheme.Muted);
        using var faint = new SolidBrush(TrayTheme.Faint);
        g.DrawString(TrayTheme.Promise, bold, muted, new RectangleF(0, top, DesignSize.Width, 18), Centered);
        g.DrawString(TrayTheme.PromiseDetail, small, faint, new RectangleF(60, top + 20, DesignSize.Width - 120, 36), Centered);
    }

    /// <summary>Draws text centered on a vertical line through <paramref name="centerX"/>.</summary>
    protected static void DrawCentered(Graphics g, string s, Font font, Brush brush, float centerX, float y)
    {
        var size = g.MeasureString(s, font);
        g.DrawString(s, font, brush, centerX - size.Width / 2, y);
    }

    /// <summary>Fonts in design-space pixels, so the DPI transform scales them with everything else.</summary>
    protected static Font DesignFont(float px, FontStyle style = FontStyle.Regular) => new("Segoe UI", px, style, GraphicsUnit.Pixel);

    protected static readonly StringFormat Centered = new() { Alignment = StringAlignment.Center };

    protected static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _logo.Dispose();
        base.OnFormClosed(e);
    }

    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
