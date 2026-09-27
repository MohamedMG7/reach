using System.Drawing;
using System.Windows.Forms;

namespace Reach.Agent.Tray;

/// <summary>Reach's colours on the PC, the same as the app's: dark, with one electric-blue accent.</summary>
public static class TrayTheme
{
    public static readonly Color Background = Color.FromArgb(0x0B, 0x0C, 0x10);
    public static readonly Color Surface = Color.FromArgb(0x16, 0x18, 0x1F);
    public static readonly Color Border = Color.FromArgb(0x2A, 0x2E, 0x3A);
    public static readonly Color Text = Color.FromArgb(0xF5, 0xF7, 0xFA);
    public static readonly Color Muted = Color.FromArgb(0x8B, 0x91, 0xA0);
    public static readonly Color Faint = Color.FromArgb(0x5A, 0x60, 0x6E);
    public static readonly Color Accent = Color.FromArgb(0x4F, 0x7C, 0xFF);
    public static readonly Color AccentSoft = Color.FromArgb(0x24, 0x31, 0x5E);
    public static readonly Color Connected = Color.FromArgb(0x30, 0xD1, 0x58);

    /// <summary>The line shown wherever Reach describes itself.</summary>
    public const string Promise = "Free · Private · Local";
    public const string PromiseDetail = "Everything stays on your home network. No accounts, no cloud, no tracking.";

    /// <summary>Renders the tray menu (and its submenus) dark, with the accent on hover.</summary>
    public sealed class MenuRenderer() : ToolStripProfessionalRenderer(new Colors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled && e.Item.ForeColor != Muted ? Text : Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Muted;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var box = new Rectangle(e.ImageRectangle.X - 1, e.ImageRectangle.Y - 1, e.ImageRectangle.Width + 2, e.ImageRectangle.Height + 2);
            using (var fill = new SolidBrush(AccentSoft)) e.Graphics.FillRectangle(fill, box);
            using var tick = new Pen(Accent, 2);
            var r = e.ImageRectangle;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(tick, [new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.5f), new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f), new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.28f)]);
        }
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => AccentSoft;
        public override Color MenuItemSelected => AccentSoft;
        public override Color MenuItemSelectedGradientBegin => AccentSoft;
        public override Color MenuItemSelectedGradientEnd => AccentSoft;
        public override Color MenuItemPressedGradientBegin => AccentSoft;
        public override Color MenuItemPressedGradientEnd => AccentSoft;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color CheckBackground => AccentSoft;
        public override Color CheckSelectedBackground => AccentSoft;
        public override Color CheckPressedBackground => AccentSoft;
    }
}
