using System.Drawing;
using System.Drawing.Drawing2D;

namespace LloydsTracker;

/// <summary>Draws the tray glyphs per state — the Windows counterpart of the macOS
/// MenuBarIcon: the Lloyds yellow rounded tile with a black glyph on top
/// (clock / clock.fill / pause / moon). The tile keeps the icon legible on both a
/// light and a dark taskbar — unlike a bare white glyph, which vanished on a light one.</summary>
internal static class TrayIconFactory
{
    private static readonly Dictionary<TrayState, Icon> Cache = new();
    private static Icon? _windowIcon;

    public static Icon For(TrayState state)
    {
        if (Cache.TryGetValue(state, out var cached)) return cached;
        var icon = Build(state);
        Cache[state] = icon;
        return icon;
    }

    /// <summary>Same tile, bigger — for window title bars, the taskbar and Alt+Tab
    /// (SummaryForm, SettingsForm). Counterpart of the macOS AppIcon.</summary>
    public static Icon Window => _windowIcon ??= Build(TrayState.Tracking, 64);

    private static Icon Build(TrayState state, int size = 32)
    {
        const int s = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            // The geometry below is written on a 32-px grid; scaling keeps it crisp at any size.
            g.ScaleTransform(size / (float)s, size / (float)s);

            // Branded tile: the Lloyds yellow rounded square. Always visible against
            // any taskbar; the state is carried by the black glyph on top. Clip to the
            // tile so no glyph (e.g. the moon's crescent bite) spills past its corners.
            using (var tileBrush = new SolidBrush(Palette.Yellow))
            using (var tile = Brand.RoundedRect(new RectangleF(1, 1, s - 2, s - 2), 7))
            {
                g.FillPath(tileBrush, tile);
                g.SetClip(tile);
            }

            Color glyph = Palette.Black;
            switch (state)
            {
                case TrayState.Paused:
                    DrawPause(g, glyph);
                    break;
                case TrayState.AwaitingReturn:
                    DrawMoon(g, glyph, Palette.Yellow);
                    break;
                case TrayState.Tracking:
                    DrawClock(g, glyph, filled: true);
                    break;
                default:
                    DrawClock(g, glyph, filled: false);
                    break;
            }
        }

        IntPtr hIcon = bmp.GetHicon();
        // Clone into a managed icon so we can destroy the native handle immediately.
        using var tmp = Icon.FromHandle(hIcon);
        var result = (Icon)tmp.Clone();
        DestroyIcon(hIcon);
        return result;
    }

    private static void DrawClock(Graphics g, Color color, bool filled)
    {
        var face = new RectangleF(4, 4, 24, 24);
        using var pen = new Pen(color, 2.6f);
        if (filled)
        {
            using var b = new SolidBrush(color.With(0.22));
            g.FillEllipse(b, face);
        }
        g.DrawEllipse(pen, face);
        var center = new PointF(16, 16);
        using var handPen = new Pen(color, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(handPen, center, new PointF(16, 8));   // minute → 12
        g.DrawLine(handPen, center, new PointF(22, 18));  // hour → ~4
    }

    private static void DrawPause(Graphics g, Color color)
    {
        using var b = new SolidBrush(color);
        using var p1 = Brand.RoundedRect(new RectangleF(9, 6, 5.5f, 20), 2);
        using var p2 = Brand.RoundedRect(new RectangleF(17.5f, 6, 5.5f, 20), 2);
        g.FillPath(b, p1);
        g.FillPath(b, p2);
    }

    private static void DrawMoon(Graphics g, Color color, Color tile)
    {
        using var b = new SolidBrush(color);
        g.FillEllipse(b, new RectangleF(5, 5, 22, 22));
        // Carve the crescent by painting the tile color back over the disc — an
        // opaque over-paint (not a transparent punch) so it blends onto the tile
        // with anti-aliased edges and never cuts a hole through to the taskbar.
        using var bite = new SolidBrush(tile);
        g.FillEllipse(bite, new RectangleF(12, 2, 22, 22));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
