using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using WinFormsTimer = System.Windows.Forms.Timer;

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
    //
    // They are SHARED: one instance per (family, size, style) for the app's lifetime. A Control
    // never disposes the Font assigned to it, and the popover, summary and prompt hints rebuild
    // their controls on every change — new fonts each time would only pile up for the finalizer.
    // So callers must never dispose a font they got from here.
    public static Font Ui(float size, FontStyle style = FontStyle.Regular) => Cached(UiFamily, size, style);
    public static Font Mono(float size, FontStyle style = FontStyle.Regular) => Cached(MonoFamily, size, style);

    private static readonly Dictionary<(string Family, float Size, FontStyle Style), Font> Fonts = new();

    private static Font Cached(string family, float size, FontStyle style)
    {
        lock (Fonts)
        {
            if (!Fonts.TryGetValue((family, size, style), out var font))
            {
                font = new Font(family, size * Dpi / 72f, style, GraphicsUnit.Pixel);
                Fonts[(family, size, style)] = font;
            }
            return font;
        }
    }

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

    /// <summary>How far an opaque child rectangle has to sit from the edge of a rounded card
    /// (corner <paramref name="radius"/>, 1 px border drawn half a pixel in) so that its corners
    /// stay inside the arc — otherwise it paints over the border and the rounded corners.</summary>
    public static int CornerInset(float radius)
        => (int)Math.Ceiling(radius + 0.5f - (radius - 0.5f) / MathF.Sqrt(2f));

    /// <summary>Window shape for the borderless pop-ups. The path is only needed to build the
    /// Region, so it's disposed here; the Region setter disposes the previous Region itself.</summary>
    public static Region RoundedRegion(Size size, float radius)
    {
        using var path = RoundedRect(new RectangleF(0, 0, size.Width, size.Height), radius);
        return new Region(path);
    }

    /// <summary>Keyboard focus ring for the hand-drawn buttons: a rounded outline just inside
    /// the control's edge. Callers draw it only when <c>Focused &amp;&amp; ShowFocusCues</c> — so it
    /// shows up once you navigate with Tab, not after a mouse click.</summary>
    public static void FocusRing(Graphics g, Size size, float radius, Color color)
    {
        float w = Sf(1.5f);
        float inset = Sf(2f);
        var r = new RectangleF(inset, inset, size.Width - inset * 2, size.Height - inset * 2);
        if (r.Width <= 0 || r.Height <= 0) return;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(r, Math.Max(0, radius - inset));
        using var pen = new Pen(color, w);
        g.DrawPath(pen, path);
        g.SmoothingMode = smoothing;
    }

    /// <summary>The date at the right of the "LLOYDS TRACKER" header steps its font down when
    /// it would run into "TRACKER" ("ponedjeljak, 26.10.2026." is the longest) — macOS:
    /// <c>minimumScaleFactor</c>.</summary>
    public static void FitDayTitle(Label day, int room)
    {
        if (day.PreferredWidth > room) day.Font = Ui(7f);
    }

    public static void EnableCrispText(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }
}

/// <summary>Gentle pop-up entrance: a fade plus a short slide, instead of the window
/// "barging in" out of nowhere.
///
/// On top of that, the window <b>doesn't take the keyboard right away</b>. A pop-up that
/// becomes active the same instant steals typing mid-word — it appears while you're typing in
/// another app and the rest of the sentence lands in the prompt's field (overwriting the
/// prefill). So the forms declare <c>ShowWithoutActivation</c> and only activate after
/// <see cref="KeyDelayMs"/>; the field focus waits exactly as long.</summary>
internal static class PanelFade
{
    public const int DurationMs = 280;
    /// <summary>How long the pop-up waits before it takes the keyboard.</summary>
    public const int KeyDelayMs = 900;

    private const int FrameMs = 15;

    /// <summary>Shows <paramref name="form"/> and animates it towards the position it lays
    /// itself out at. <paramref name="slide"/> is the starting Y offset (positive = drops in
    /// from above). <paramref name="keyDelayMs"/> of 0 means "take the keyboard right away";
    /// <paramref name="onKeyboard"/> runs the moment the window becomes active.</summary>
    public static void Appear(Form form, int slide = 10, int keyDelayMs = KeyDelayMs, Action? onKeyboard = null)
    {
        form.Opacity = 0;
        // ShowWithoutActivation keeps the keyboard wherever it was; Show() only maps the window.
        form.Show();
        if (form.IsDisposed) return;

        var target = form.Location;
        int offset = Brand.S(slide);
        form.Location = new Point(target.X, target.Y - offset);

        var start = DateTime.Now;
        var fade = new WinFormsTimer { Interval = FrameMs };
        fade.Tick += (_, _) =>
        {
            if (form.IsDisposed || !form.Visible)
            {
                fade.Stop();
                fade.Dispose();
                return;
            }
            double t = Math.Clamp((DateTime.Now - start).TotalMilliseconds / DurationMs, 0, 1);
            // easeOut, like the CAMediaTimingFunction on the macOS side.
            double eased = 1 - Math.Pow(1 - t, 3);
            form.Opacity = eased;
            form.Location = new Point(target.X, target.Y - (int)Math.Round(offset * (1 - eased)));
            if (t >= 1)
            {
                fade.Stop();
                fade.Dispose();
                form.Opacity = 1;
                form.Location = target;
            }
        };
        fade.Start();

        if (keyDelayMs <= 0)
        {
            form.Activate();
            onKeyboard?.Invoke();
            return;
        }
        var arm = new WinFormsTimer { Interval = keyDelayMs };
        arm.Tick += (_, _) =>
        {
            arm.Stop();
            arm.Dispose();
            if (form.IsDisposed || !form.Visible) return;
            form.Activate();
            onKeyboard?.Invoke();
        };
        arm.Start();
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
        // Decoration only — no Tab stop (otherwise Tab stops on "OD"/"DO", "LLOYDS"…).
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
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
    /// <summary>Podloga pod mišem umjesto automatski izračunate (npr. žuta pločica iza strelice
    /// koja inače nema podlogu).</summary>
    public Color? HoverFill { get; set; }

    /// <summary>Width of <paramref name="text"/> as <see cref="OnPaint"/> draws it (GDI+
    /// <c>MeasureString</c>, ClearType grid-fit). Size buttons with this, not
    /// <c>TextRenderer.MeasureText</c> — GDI measures narrower and the text gets clipped.</summary>
    public static int TextWidth(string text, Font font)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        Brand.EnableCrispText(g);
        return (int)Math.Ceiling(g.MeasureString(text, font).Width);
    }

    private bool _hover;
    private bool _down;

    // ControlPaint.Light/Dark vraćaju neprozirnu boju — prozirna žuta podloga (12 %) bi pod
    // mišem postala puna žuta i progutala žuti tekst. Zato prozirna podloga samo malo
    // pojača pokrivenost, a puna se pomiješa prema bijeloj/crnoj.
    private static Color Hovered(Color c) => c.A < 255 ? Stronger(c, 20) : Mix(c, Color.White, 0.08);
    private static Color Pressed(Color c) => c.A < 255 ? Stronger(c, 36) : Mix(c, Color.Black, 0.08);

    private static Color Stronger(Color c, int alpha) => Color.FromArgb(Math.Min(255, c.A + alpha), c.R, c.G, c.B);

    private static Color Mix(Color c, Color to, double t) => Color.FromArgb(
        c.A,
        (int)Math.Round(c.R + (to.R - c.R) * t),
        (int)Math.Round(c.G + (to.G - c.G) * t),
        (int)Math.Round(c.B + (to.B - c.B) * t));

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        // Svaki klik je Click — inače drugi brzi klik (strelice ◀ ▶, +5 min…) postane
        // DoubleClick i izgubi se.
        SetStyle(ControlStyles.StandardDoubleClick, false);
        Cursor = Cursors.Hand;
        Font = Brand.Ui(9.5f, FontStyle.Bold);
        BackColor = Palette.Black;
    }

    /// <summary>Gets Tab focus and reacts to Enter/Space like a real Button (default). Pop-ups
    /// that appear on their own (prompt, reminder, auto-stop, update) turn it off: they take the
    /// keyboard while you may still be typing elsewhere, and a stray Space/Enter must not press
    /// "Start" or "+30 min" for you.</summary>
    public bool Focusable
    {
        get => GetStyle(ControlStyles.Selectable);
        set { SetStyle(ControlStyles.Selectable, value); TabStop = value; }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    // MARK: - Keyboard (Enter clicks on press, Space on release — like a Win32 button)

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // SuppressKeyPress: the click may move focus into a text field (e.g. "Dodaj unos" opens
        // the editor) — the Enter/Space character must not land there.
        if (e.Modifiers == Keys.None && e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            OnClick(EventArgs.Empty);
            return;
        }
        if (e.Modifiers == Keys.None && e.KeyCode == Keys.Space)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            _down = true;
            Invalidate();
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && _down)
        {
            e.Handled = true;
            _down = false;
            Invalidate();
            OnClick(EventArgs.Empty);
            return;
        }
        base.OnKeyUp(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { _down = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnChangeUICues(UICuesEventArgs e) { Invalidate(); base.OnChangeUICues(e); }

    // Gumb onemogućen ispod miša (▶ na zadnjem izboru / na današnjem danu) ne dobije
    // MouseLeave — bez ovoga bi ostao obojan kao da je miš na njemu.
    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) { _hover = false; _down = false; }
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Brand.EnableCrispText(g);
        g.Clear(BackColor);

        var r = new RectangleF(BorderWidth / 2f, BorderWidth / 2f, Width - BorderWidth, Height - BorderWidth);
        using var path = Brand.RoundedRect(r, Brand.Sf(CornerRadius));

        bool hover = _hover && Enabled;
        var fill = hover && HoverFill is Color hoverFill ? hoverFill : Fill;
        if (fill.A > 0)
        {
            if (_down && Enabled) fill = Pressed(fill);
            else if (hover && HoverFill == null) fill = Hovered(fill);
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
        }
        if (BorderWidth > 0 && BorderColor.A > 0)
        {
            using var pen = new Pen(BorderColor, BorderWidth);
            g.DrawPath(pen, path);
        }
        // Opaque fill (yellow Start/Spremi) gets a dark ring inside it, everything else a yellow one.
        if (Focused && ShowFocusCues)
            Brand.FocusRing(g, ClientSize, Brand.Sf(CornerRadius), fill.A == 255 ? Palette.Black : Palette.Yellow);

        // Icon + text, centered as a group.
        var textColor = hover && fill.A == 0 ? ControlPaint.Light(TextColor, 0.3f) : TextColor;
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
/// (mirrors NSWindow.isMovableByWindowBackground). <paramref name="onMoved"/> runs after every
/// drag step — for a form that lays itself out again later and must stay where it was dropped.</summary>
internal static class FormDrag
{
    public static void Enable(Form form, Control surface, Action? onMoved = null)
    {
        Point offset = Point.Empty;
        bool dragging = false;
        surface.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) { dragging = true; offset = e.Location; }
        };
        surface.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            form.Location = new Point(form.Location.X + e.X - offset.X, form.Location.Y + e.Y - offset.Y);
            onMoved?.Invoke();
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
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
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
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
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

/// <summary>Dark-themed drop-down. A WinForms ComboBox ignores BackColor unless it's
/// owner-drawn, so both the closed box and the list are painted by hand. Shared by
/// Postavke and the entry-edit window.</summary>
internal static class Dark
{
    public static ComboBox Combo(
        string[] items,
        int selectedIndex,
        Point location,
        int width,
        AnchorStyles anchor = AnchorStyles.Top | AnchorStyles.Left)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Palette.White.OverBlack(0.1),
            ForeColor = Palette.White,
            Font = Brand.Ui(9.5f),
            DrawMode = DrawMode.OwnerDrawFixed,
            Location = location,
            Width = width,
            Anchor = anchor,
        };
        combo.Items.AddRange(items.Cast<object>().ToArray());
        combo.DrawItem += DrawComboItem;
        // Windows sends the wheel to the control under the mouse — scrolling the summary or
        // Postavke over a closed drop-down would change its value along the way. Closed, it
        // scrolls the surrounding page instead; open, the list scrolls as usual.
        combo.MouseWheel += (_, e) =>
        {
            if (combo.DroppedDown || e is not HandledMouseEventArgs handled) return;
            handled.Handled = true;
            ScrollAncestor(combo, e.Delta);
        };
        if (selectedIndex >= 0 && selectedIndex < items.Length) combo.SelectedIndex = selectedIndex;
        return combo;
    }

    /// <summary>Width that fits the longest item (plus the list's scrollbar), at least the
    /// combo's own width and at most <paramref name="max"/>; longer items end in "…".</summary>
    public static int DropDownWidthFor(ComboBox combo, int max)
    {
        int widest = 0;
        foreach (var item in combo.Items)
            widest = Math.Max(widest, TextRenderer.MeasureText(item?.ToString() ?? "", combo.Font).Width);
        int width = widest + Brand.S(12) + SystemInformation.VerticalScrollBarWidth;
        return Math.Clamp(width, combo.Width, Math.Max(combo.Width, max));
    }

    /// <summary>Scrolls the nearest auto-scrolling ancestor by one wheel step (as many lines as
    /// the system wheel setting says, or a page).</summary>
    private static void ScrollAncestor(Control control, int delta)
    {
        for (var c = control.Parent; c != null; c = c.Parent)
        {
            if (c is not ScrollableControl { AutoScroll: true } sc || !sc.VerticalScroll.Visible) continue;
            int lines = SystemInformation.MouseWheelScrollLines;
            int step = lines > 0 ? lines * control.Font.Height : sc.ClientSize.Height;
            var pos = sc.AutoScrollPosition; // negative offsets
            int y = Math.Max(0, -pos.Y - delta * step / 120);
            sc.AutoScrollPosition = new Point(-pos.X, y);
            return;
        }
    }

    private static void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo || e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using var back = new SolidBrush(selected ? Palette.Yellow.With(0.25) : Palette.White.OverBlack(0.1));
        e.Graphics.FillRectangle(back, e.Bounds);
        TextRenderer.DrawText(e.Graphics, combo.Items[e.Index]?.ToString() ?? "", combo.Font, e.Bounds, Palette.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
