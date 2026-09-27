using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using QRCoder;
using Reach.Agent.Pairing;

namespace Reach.Agent.Tray;

/// <summary>Shows the pairing QR code until it is used or expires (spec §3.3).</summary>
public sealed class PairWindow : ReachWindow
{
    private static readonly RectangleF Card = new(76, 124, 248, 248);
    private const string Steps = "Open Reach  ·  tap Pair  ·  scan this code";

    private readonly PairingService _pairing;
    private readonly PairingCode _code;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;
    private readonly bool _hasNetwork;
    private readonly Image _qr;
    private readonly System.Windows.Forms.Timer _countdown;
    private bool _paired;

    public PairWindow(PairingService pairing, string pairingUri, PairingCode code, TimeProvider time, bool hasNetwork)
        : base("Pair a phone with Reach", new Size(400, 570))
    {
        _pairing = pairing;
        _code = code;
        _time = time;
        _hasNetwork = hasNetwork;
        _lifetime = code.ExpiresAt - time.GetUtcNow();
        _qr = RenderQr(pairingUri);

        _countdown = new System.Windows.Forms.Timer { Interval = 250 };
        _countdown.Tick += (_, _) => Tick();
        _pairing.Paired += OnPaired;
        _countdown.Start();
    }

    protected override string Heading => "Pair a phone";

    protected override void PaintContent(Graphics g)
    {
        using var body = DesignFont(14);
        using var small = DesignFont(12.5f);
        using var text = new SolidBrush(TrayTheme.Text);
        using var muted = new SolidBrush(TrayTheme.Muted);
        using var accent = new SolidBrush(TrayTheme.Accent);

        if (!_hasNetwork)
        {
            using var heading = DesignFont(17, FontStyle.Bold);
            g.DrawString("No home network", heading, text, new RectangleF(40, 200, DesignSize.Width - 80, 30), Centered);
            g.DrawString("This PC isn't connected to Wi-Fi or Ethernet.\nConnect it, then choose \"Pair new phone…\" again.", body, muted,
                new RectangleF(40, 240, DesignSize.Width - 80, 80), Centered);
            return;
        }

        // The code on a plain white square: the camera needs the contrast.
        using (var card = Rounded(Card, 18))
        using (var white = new SolidBrush(Color.White))
            g.FillPath(white, card);
        g.InterpolationMode = InterpolationMode.NearestNeighbor; // QR modules stay sharp
        g.DrawImage(_qr, RectangleF.Inflate(Card, -14, -14));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // Time left: a thin line under the code, and the minutes and seconds.
        var left = _code.ExpiresAt - _time.GetUtcNow();
        var fraction = _lifetime > TimeSpan.Zero ? (float)Math.Clamp(left / _lifetime, 0, 1) : 0;
        var track = new RectangleF(Card.Left, Card.Bottom + 20, Card.Width, 3);
        using (var trackBrush = new SolidBrush(TrayTheme.Border))
            g.FillRectangle(trackBrush, track);
        if (fraction > 0)
            g.FillRectangle(accent, track.Left, track.Top, track.Width * fraction, track.Height);
        var expires = $"Expires in {(left > TimeSpan.Zero ? left : TimeSpan.Zero):m\\:ss}";
        g.DrawString(expires, small, muted, new RectangleF(0, track.Bottom + 8, DesignSize.Width, 20), Centered);

        g.DrawString(Steps, body, text, new RectangleF(0, track.Bottom + 48, DesignSize.Width, 22), Centered);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _countdown.Dispose();
        _pairing.Paired -= OnPaired;
        if (!_paired) _pairing.Cancel();
        _qr.Dispose();
        base.OnFormClosed(e);
    }

    // Raised on the connection's thread.
    private void OnPaired()
    {
        _paired = true;
        if (IsHandleCreated) BeginInvoke(Close);
    }

    private void Tick()
    {
        if (_code.ExpiresAt <= _time.GetUtcNow()) Close();
        else Invalidate();
    }

    private static Image RenderQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10, drawQuietZones: false);
        return Image.FromStream(new MemoryStream(png));
    }
}
