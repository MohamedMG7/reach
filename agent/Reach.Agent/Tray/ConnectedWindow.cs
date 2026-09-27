using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Reach.Agent.Tray;

/// <summary>Which phone is connected and since when, with a Disconnect button. Shown instead of the QR code while connected.</summary>
public sealed class ConnectedWindow : ReachWindow
{
    private static readonly RectangleF Button = new(136, 274, 128, 40);
    private static readonly Color Danger = Color.FromArgb(0xFF, 0x45, 0x3A);

    private readonly DateTimeOffset _since;
    private readonly TimeProvider _time;
    private readonly Action _disconnect;
    private readonly System.Windows.Forms.Timer _clock;
    private string _deviceName;
    private bool _hover;

    public ConnectedWindow(string deviceName, DateTimeOffset since, TimeProvider time, Action disconnect)
        : base("Reach", new Size(400, 410))
    {
        _deviceName = deviceName;
        _since = since;
        _time = time;
        _disconnect = disconnect;
        _clock = new System.Windows.Forms.Timer { Interval = 1000 };
        _clock.Tick += (_, _) => Invalidate();
        _clock.Start();
    }

    /// <summary>The phone's name arrives with its hello, just after the connection.</summary>
    public void SetDeviceName(string name)
    {
        _deviceName = name;
        Invalidate();
    }

    /// <summary>How long the phone has been connected, to the second: "0:42", "12:04", "1:02:45".</summary>
    public static string Elapsed(DateTimeOffset since, DateTimeOffset now)
    {
        var d = now - since;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        return d < TimeSpan.FromHours(1) ? $"{d.Minutes}:{d.Seconds:00}" : $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}";
    }

    /// <summary>"since 17:42" in local time.</summary>
    public static string SinceText(DateTimeOffset since) => $"since {since.ToLocalTime():HH:mm}";

    protected override void PaintContent(Graphics g)
    {
        var middle = DesignSize.Width / 2f;
        using var name = DesignFont(20, FontStyle.Bold);
        using var small = DesignFont(13);
        using var text = new SolidBrush(TrayTheme.Text);
        using var muted = new SolidBrush(TrayTheme.Muted);
        using var green = new SolidBrush(TrayTheme.Connected);

        using (var oneLine = new StringFormat(Centered) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(_deviceName, name, text, new RectangleF(24, 86, DesignSize.Width - 48, 30), oneLine);

        // ● Connected
        using (var status = DesignFont(13, FontStyle.Bold))
        {
            var width = g.MeasureString("Connected", status).Width;
            var left = middle - (width + 14) / 2;
            g.FillEllipse(green, left, 125, 8, 8);
            g.DrawString("Connected", status, green, left + 12, 119);
        }

        // The live timer, the main thing on the window.
        using (var timer = new Font("Segoe UI Light", 58, FontStyle.Regular, GraphicsUnit.Pixel))
            DrawCentered(g, Elapsed(_since, _time.GetUtcNow()), timer, text, middle, 148);
        DrawCentered(g, SinceText(_since), small, muted, middle, 226);

        // Disconnect: a quiet red pill that fills in on hover.
        using (var button = Rounded(Button, Button.Height / 2))
        {
            if (_hover)
            {
                using var fill = new SolidBrush(Color.FromArgb(0x33, Danger));
                g.FillPath(fill, button);
            }
            using var border = new Pen(Color.FromArgb(_hover ? 0xC0 : 0x55, Danger));
            g.DrawPath(border, button);
        }
        using var label = DesignFont(14, FontStyle.Bold);
        using var danger = new SolidBrush(Danger);
        var size = g.MeasureString("Disconnect", label);
        g.DrawString("Disconnect", label, danger, Button.Left + (Button.Width - size.Width) / 2, Button.Top + (Button.Height - size.Height) / 2);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = Button.Contains(ToDesign(e.Location));
        if (hover == _hover) return;
        _hover = hover;
        Cursor = hover ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && Button.Contains(ToDesign(e.Location))) _disconnect();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _clock.Dispose();
        base.OnFormClosed(e);
    }
}
