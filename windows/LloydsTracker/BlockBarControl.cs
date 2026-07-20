using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LloydsTracker;

/// <summary>Period-splitting geometry, ported verbatim from PromptView (SwiftUI).</summary>
internal static class PromptGeometry
{
    private static DateTime HourStart(DateTime d) => new(d.Year, d.Month, d.Day, d.Hour, 0, 0, d.Kind);

    /// <summary>5-min grid points strictly inside the period (min. 2 min from the edges).
    /// For very long periods the grid is thinned so there are never more than ~12 blocks.</summary>
    public static List<DateTime> GridBoundaries(DateTime start, DateTime end)
    {
        var empty = new List<DateTime>();
        if ((end - start).TotalSeconds <= 240) return empty;
        var hourStart = HourStart(start);

        foreach (int stepMinutes in new[] { 5, 10, 15, 30, 60 })
        {
            double step = stepMinutes * 60;
            var t = hourStart;
            while (t <= start.AddSeconds(120)) t = t.AddSeconds(step);
            var points = new List<DateTime>();
            while (t <= end.AddSeconds(-120))
            {
                points.Add(t);
                t = t.AddSeconds(step);
            }
            if (points.Count <= 11) return points;
        }
        return empty;
    }

    public static List<(DateTime Start, DateTime End)> Segments(
        DateTime periodStart, DateTime periodEnd, IReadOnlyList<DateTime> boundaries, ISet<DateTime> splitPoints)
    {
        var result = new List<(DateTime, DateTime)>();
        var s = periodStart;
        foreach (var p in boundaries)
        {
            if (splitPoints.Contains(p))
            {
                result.Add((s, p));
                s = p;
            }
        }
        result.Add((s, periodEnd));
        return result;
    }
}

/// <summary>The interactive block bar: yellow segment rects, scissor/merge handles at each
/// grid boundary, and a time-labels row. Mirrors PromptView.blockBar.</summary>
internal sealed class BlockBarControl : Control
{
    private const int TrackHeight = 20;
    private const int Gap = 3;
    private const int LabelsHeight = 11;
    private const float HandleRadius = 10f;

    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public IReadOnlyList<DateTime> Boundaries { get; set; } = Array.Empty<DateTime>();
    public HashSet<DateTime> SplitPoints { get; set; } = new();
    public DateTime? FocusedStart { get; set; }
    public bool SingleSegment { get; set; }

    public event Action<DateTime>? SplitToggled;
    public event Action<DateTime>? SegmentFocused;

    private readonly ToolTip _tip = new();

    public BlockBarControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Black;
        Height = TrackHeight + Gap + LabelsHeight;
        Cursor = Cursors.Hand;
    }

    private double Total => Math.Max((PeriodEnd - PeriodStart).TotalSeconds, 1);
    private float XOf(DateTime d) => (float)((d - PeriodStart).TotalSeconds / Total) * Width;

    private List<(DateTime Start, DateTime End)> CurrentSegments()
        => PromptGeometry.Segments(PeriodStart, PeriodEnd, Boundaries, SplitPoints);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Brand.EnableCrispText(g);
        g.Clear(BackColor);

        var segments = CurrentSegments();

        // Segment rectangles.
        foreach (var seg in segments)
        {
            float x = XOf(seg.Start);
            float sw = (float)((seg.End - seg.Start).TotalSeconds / Total) * Width;
            bool active = FocusedStart == seg.Start || segments.Count == 1;
            using var b = new SolidBrush(Palette.Yellow.OverBlack(active ? 0.85 : 0.4));
            var rect = new RectangleF(x + 2, 0, Math.Max(6, sw - 4), TrackHeight);
            using var path = Brand.RoundedRect(rect, 6);
            g.FillPath(b, path);
        }

        // Split handles.
        foreach (var b in Boundaries)
        {
            float x = XOf(b);
            bool isSplit = SplitPoints.Contains(b);
            var center = new PointF(x, TrackHeight / 2f);
            var circle = new RectangleF(center.X - HandleRadius, center.Y - HandleRadius, HandleRadius * 2, HandleRadius * 2);
            using (var fill = new SolidBrush(Palette.Black)) g.FillEllipse(fill, circle);
            using (var pen = new Pen(isSplit ? Palette.Yellow : Palette.White.With(0.35), 1f)) g.DrawEllipse(pen, circle);

            using var glyphPen = new Pen(isSplit ? Palette.Yellow : Palette.Gray, 1.4f);
            if (isSplit)
            {
                // "×" — click to merge.
                g.DrawLine(glyphPen, center.X - 3, center.Y - 3, center.X + 3, center.Y + 3);
                g.DrawLine(glyphPen, center.X - 3, center.Y + 3, center.X + 3, center.Y - 3);
            }
            else
            {
                // Cut tick — click to split here.
                g.DrawLine(glyphPen, center.X, center.Y - 4, center.X, center.Y + 4);
            }
        }

        // Time labels row.
        int labelsTop = TrackHeight + Gap;
        using var labelFont = Brand.Mono(6.5f);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
        foreach (var b in Boundaries)
        {
            float x = XOf(b);
            bool isSplit = SplitPoints.Contains(b);
            using var brush = new SolidBrush(isSplit ? Palette.Yellow : Palette.Gray.OverBlack(0.55));
            g.DrawString(Fmt.Hhmm(b), labelFont, brush, new RectangleF(x - 20, labelsTop, 40, LabelsHeight), sf);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        // Handles take priority over segment focus.
        foreach (var b in Boundaries)
        {
            var center = new PointF(XOf(b), TrackHeight / 2f);
            if (Dist(e.Location, center) <= HandleRadius + 2)
            {
                SplitToggled?.Invoke(b);
                return;
            }
        }

        if (e.Y <= TrackHeight)
        {
            foreach (var seg in CurrentSegments())
            {
                float x = XOf(seg.Start);
                float sw = (float)((seg.End - seg.Start).TotalSeconds / Total) * Width;
                if (e.X >= x && e.X <= x + sw)
                {
                    SegmentFocused?.Invoke(seg.Start);
                    return;
                }
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        foreach (var b in Boundaries)
        {
            var center = new PointF(XOf(b), TrackHeight / 2f);
            if (Dist(e.Location, center) <= HandleRadius + 2)
            {
                string text = SplitPoints.Contains(b) ? "Spoji blokove" : $"Razdvoji u {Fmt.Hhmm(b)}";
                if (_tip.GetToolTip(this) != text) _tip.SetToolTip(this, text);
                return;
            }
        }
        if (!string.IsNullOrEmpty(_tip.GetToolTip(this))) _tip.SetToolTip(this, "");
    }

    private static float Dist(Point p, PointF c) => (float)Math.Sqrt(Math.Pow(p.X - c.X, 2) + Math.Pow(p.Y - c.Y, 2));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
