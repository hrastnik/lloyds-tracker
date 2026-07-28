using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace LloydsTracker;

/// <summary>Brand fonts and small drawing helpers shared across the WinForms UI.</summary>
internal static class Brand
{
    public const string UiFamily = "Segoe UI";
    public const string MonoFamily = "Consolas";

    /// <summary>System DPI captured once at startup (96 = 100% scaling). Set from Program
    /// before any UI is created. Every layout dimension and font is scaled from this so
    /// the UI renders at the right physical size — crisply — on high-DPI displays.</summary>
    public static int Dpi = 96;

    /// <summary>96-DPI-design → device scale factor (1.0 at 100%, 1.5 at 150%).</summary>
    public static float Scale => Dpi / 96f;

    /// <summary>Scale a design (96-DPI) pixel measurement to the current DPI.</summary>
    public static int S(int px) => (int)Math.Round(px * Scale);

    /// <summary>Float variant of <see cref="S(int)"/> for pen widths, radii and offsets.</summary>
    public static float Sf(float px) => px * Scale;

    // Fonts are built in PIXEL units at the system DPI (point size → px at Dpi) so that
    // text measurement (Font.Height, TextRenderer/Graphics.MeasureString) and rendering
    // agree exactly at any DPI. Call sites keep passing the same point sizes.
    public static Font Ui(float size, FontStyle style = FontStyle.Regular) => new(UiFamily, size * Dpi / 72f, style, GraphicsUnit.Pixel);
    public static Font Mono(float size, FontStyle style = FontStyle.Regular) => new(MonoFamily, size * Dpi / 72f, style, GraphicsUnit.Pixel);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(r);
            path.CloseFigure();
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void EnableCrispText(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }
}

/// <summary>A label that draws its text with fixed inter-character spacing (tracking),
/// reproducing the letter-spaced brand wordmarks from the SwiftUI version.</summary>
internal sealed class TrackedLabel : Control
{
    private float _tracking = 2f;

    public TrackedLabel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        AutoSize = false;
    }

    public float Tracking
    {
        get => _tracking;
        set { _tracking = value; Recalc(); Invalidate(); }
    }

    [AllowNull]
    public override string Text
    {
        get => base.Text;
        set { base.Text = value; Recalc(); Invalidate(); }
    }

    [AllowNull]
    public override Font Font
    {
        get => base.Font;
        set { base.Font = value; Recalc(); Invalidate(); }
    }

    private void Recalc()
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        float w = 0;
        float tracking = _tracking * Brand.Scale;
        foreach (char c in Text ?? "")
            w += g.MeasureString(c.ToString(), Font, PointF.Empty, StringFormat.GenericTypographic).Width + tracking;
        int h = Font.Height;
        Size = new Size((int)Math.Ceiling(Math.Max(0, w)), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Brand.EnableCrispText(e.Graphics);
        float x = 0;
        float tracking = _tracking * Brand.Scale;
        using var brush = new SolidBrush(ForeColor);
        foreach (char c in Text ?? "")
        {
            string s = c.ToString();
            e.Graphics.DrawString(s, Font, brush, new PointF(x, 0), StringFormat.GenericTypographic);
            x += e.Graphics.MeasureString(s, Font, PointF.Empty, StringFormat.GenericTypographic).Width + tracking;
        }
    }
}

/// <summary>Flat, brand-styled button that renders correctly on the dark background
/// (the default WinForms button chrome disappears on near-black).</summary>
internal sealed class FlatButton : Control
{
    public Color Fill { get; set; } = Color.Transparent;
    public Color BorderColor { get; set; } = Color.Transparent;
    public Color TextColor { get; set; } = Palette.Gray;
    public float CornerRadius { get; set; } = 8f;
    public float BorderWidth { get; set; } = 0f;
    public Image? Glyph { get; set; }
    public FontStyle TextStyle { get; set; } = FontStyle.Bold;
    public ContentAlignment Align { get; set; } = ContentAlignment.MiddleCenter;
    /// <summary>Optional second, smaller line centered under <see cref="Control.Text"/>
    /// (used by the auto-stop chips: "+30 min" over the resulting time).</summary>
    public string? SubText { get; set; }
    public Font? SubFont { get; set; }
    public Color SubColor { get; set; } = Palette.Gray;

    private bool _hover;
    private bool _down;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        Font = Brand.Ui(9.5f, FontStyle.Bold);
        BackColor = Palette.Black;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Brand.EnableCrispText(g);
        g.Clear(BackColor);

        var r = new RectangleF(BorderWidth / 2f, BorderWidth / 2f, Width - BorderWidth, Height - BorderWidth);
        using var path = Brand.RoundedRect(r, Brand.Sf(CornerRadius));

        var fill = Fill;
        if (fill.A > 0)
        {
            if (_down) fill = ControlPaint.Dark(fill, 0.05f);
            else if (_hover) fill = ControlPaint.Light(fill, 0.08f);
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
        }
        if (BorderWidth > 0 && BorderColor.A > 0)
        {
            using var pen = new Pen(BorderColor, BorderWidth);
            g.DrawPath(pen, path);
        }

        // Icon + text, centered as a group.
        var textColor = _hover && fill.A == 0 ? ControlPaint.Light(TextColor, 0.3f) : TextColor;
        var font = Font;

        // Two-line variant: main text over a smaller sub-line, both centered.
        if (!string.IsNullOrEmpty(SubText))
        {
            var subFont = SubFont ?? font;
            SizeF mainSize = g.MeasureString(Text, font);
            SizeF subSize = g.MeasureString(SubText, subFont);
            float blockH = mainSize.Height + subSize.Height;
            float top = (Height - blockH) / 2f;
            using var mainBrush = new SolidBrush(textColor);
            using var subBrush = new SolidBrush(SubColor);
            g.DrawString(Text, font, mainBrush, new PointF((Width - mainSize.Width) / 2f, top));
            g.DrawString(SubText, subFont, subBrush, new PointF((Width - subSize.Width) / 2f, top + mainSize.Height));
            return;
        }

        SizeF textSize = string.IsNullOrEmpty(Text) ? SizeF.Empty : g.MeasureString(Text, font);
        int glyphW = Glyph != null ? Glyph.Width + Brand.S(6) : 0;
        float totalW = textSize.Width + glyphW;
        float startX = Align switch
        {
            ContentAlignment.MiddleLeft => Brand.S(10),
            ContentAlignment.MiddleRight => Width - totalW - Brand.S(10),
            _ => (Width - totalW) / 2f
        };
        float y = (Height - Math.Max(textSize.Height, Glyph?.Height ?? 0)) / 2f;

        if (Glyph != null)
        {
            g.DrawImage(Glyph, startX, (Height - Glyph.Height) / 2f, Glyph.Width, Glyph.Height);
            startX += glyphW;
        }
        if (!string.IsNullOrEmpty(Text))
        {
            using var tb = new SolidBrush(textColor);
            g.DrawString(Text, font, tb, new PointF(startX, y));
        }
    }
}

/// <summary>Lets a borderless form be dragged by clicking anywhere on the given control
/// (mirrors NSWindow.isMovableByWindowBackground).</summary>
internal static class FormDrag
{
    public static void Enable(Form form, Control surface)
    {
        Point offset = Point.Empty;
        bool dragging = false;
        surface.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) { dragging = true; offset = e.Location; }
        };
        surface.MouseMove += (_, e) =>
        {
            if (dragging)
                form.Location = new Point(form.Location.X + e.X - offset.X, form.Location.Y + e.Y - offset.Y);
        };
        surface.MouseUp += (_, _) => dragging = false;
    }
}

/// <summary>A filled circle (status dot / brand bullet).</summary>
internal sealed class Dot : Control
{
    public Color Color { get; set; } = Palette.Yellow;

    public Dot()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        using var b = new SolidBrush(Color);
        g.FillEllipse(b, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>A simple keyboard-key chip ("⏎", "esc") used in the prompt hint row.</summary>
internal sealed class KeyChip : Control
{
    public KeyChip(string text, Color bg, Color fg)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Text = text;
        BackColor = bg;
        ForeColor = fg;
        Font = Brand.Mono(7.5f, FontStyle.Bold);
        var sz = TextRenderer.MeasureText(text, Font);
        Size = new Size(sz.Width + Brand.S(10), sz.Height + Brand.S(4));
    }

    public Color ChipColor { get; set; } = Palette.White.OverBlack(0.12);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Brand.EnableCrispText(g);
        g.Clear(BackColor);
        using (var b = new SolidBrush(ChipColor))
        using (var path = Brand.RoundedRect(new RectangleF(0, 0, Width, Height), Brand.Sf(4)))
            g.FillPath(b, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>A borderless double-buffered panel that paints a rounded, bordered card
/// on the dark background.</summary>
internal sealed class CardPanel : Panel
{
    public Color CardFill { get; set; } = Palette.Black;
    public Color CardBorder { get; set; } = Palette.Yellow.With(0.35);
    public float Radius { get; set; } = 16f;
    public float BorderWidth { get; set; } = 1f;

    public CardPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Black;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var r = new RectangleF(BorderWidth / 2f, BorderWidth / 2f, Width - BorderWidth, Height - BorderWidth);
        using var path = Brand.RoundedRect(r, Brand.Sf(Radius));
        using (var b = new SolidBrush(CardFill)) g.FillPath(b, path);
        if (BorderWidth > 0)
        {
            using var pen = new Pen(CardBorder, BorderWidth);
            g.DrawPath(pen, path);
        }
    }
}
