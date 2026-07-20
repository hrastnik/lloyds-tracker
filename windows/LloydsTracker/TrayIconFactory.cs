using System.Drawing;
using System.Drawing.Drawing2D;

namespace LloydsTracker;

/// <summary>Draws the tray glyphs per state — the Windows counterpart of the macOS
/// SF Symbols (clock / clock.fill / pause / moon.zzz).</summary>
internal static class TrayIconFactory
{
    private static readonly Dictionary<TrayState, Icon> Cache = new();

    public static Icon For(TrayState state)
    {
        if (Cache.TryGetValue(state, out var cached)) return cached;
        var icon = Build(state);
        Cache[state] = icon;
        return icon;
    }

    private static Icon Build(TrayState state)
    {
        const int s = 32;
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Color color = state switch
            {
                TrayState.Tracking => Palette.Yellow,
                TrayState.Paused => Palette.Orange,
                TrayState.AwaitingReturn => Palette.Blue,
                _ => Palette.White
            };

            switch (state)
            {
                case TrayState.Paused:
                    DrawPause(g, color);
                    break;
                case TrayState.AwaitingReturn:
                    DrawMoon(g, color);
                    break;
                case TrayState.Tracking:
                    DrawClock(g, color, filled: true);
                    break;
                default:
                    DrawClock(g, color, filled: false);
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

    private static void DrawMoon(Graphics g, Color color)
    {
        using var b = new SolidBrush(color);
        var prev = g.CompositingMode;
        g.FillEllipse(b, new RectangleF(5, 5, 22, 22));
        // Punch a hole to make the crescent.
        g.CompositingMode = CompositingMode.SourceCopy;
        using var hole = new SolidBrush(Color.Transparent);
        g.FillEllipse(hole, new RectangleF(12, 2, 22, 22));
        g.CompositingMode = prev;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
