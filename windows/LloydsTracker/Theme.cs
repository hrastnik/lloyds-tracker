using System.Drawing;
using System.Globalization;

namespace LloydsTracker;

/// <summary>Lloyds Digital brand — https://lloyds-digital.com/</summary>
internal static class Palette
{
    public static readonly Color Yellow = Color.FromArgb(0xFB, 0xDE, 0x07); // #FBDE07
    public static readonly Color Black = Color.FromArgb(0x07, 0x07, 0x07);  // #070707
    public static readonly Color Gray = Color.FromArgb(0xC4, 0xC4, 0xC4);   // #C4C4C4
    public static readonly Color White = Color.White;
    public static readonly Color StopRed = Color.FromArgb(255, 92, 79);     // rgb(1.0, 0.36, 0.31)
    public static readonly Color Orange = Color.FromArgb(255, 149, 0);
    public static readonly Color Blue = Color.FromArgb(10, 132, 255);

    public static Color With(this Color c, double alpha)
        => Color.FromArgb((int)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

    /// <summary>Flatten a semi-transparent brand color over the black background,
    /// so GDI brushes that don't blend still read correctly.</summary>
    public static Color OverBlack(this Color c, double alpha) => c.Over(Black, alpha);

    /// <summary>Flatten a semi-transparent color over the given (opaque) background. A plain
    /// <see cref="System.Windows.Forms.Label"/> draws with GDI, which ignores the alpha of its
    /// ForeColor — a dimmed label needs the flattened color.</summary>
    public static Color Over(this Color c, Color background, double alpha)
    {
        double a = Math.Clamp(alpha, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(c.R * a + background.R * (1 - a)),
            (int)Math.Round(c.G * a + background.G * (1 - a)),
            (int)Math.Round(c.B * a + background.B * (1 - a)));
    }
}

/// <summary>Croatian-locale date/time formatting — mirrors the macOS Fmt enum.</summary>
internal static class Fmt
{
    public static readonly CultureInfo Hr = CultureInfo.GetCultureInfo("hr-HR");
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Hhmm(DateTime d) => d.ToString("HH:mm", Hr);

    public static string DayKey(DateTime d) => d.ToString("yyyy-MM-dd", Invariant);

    // Croatian: "ponedjeljak, 20.7.2026." — EEEE, d.M.yyyy.
    public static string DayTitle(DateTime d) => d.ToString("dddd, d.M.yyyy.", Hr);

    /// <summary>Real seconds from <paramref name="start"/> to <paramref name="end"/>. Local
    /// DateTime subtraction is wall-clock arithmetic, so across a DST switch it'd be an hour
    /// off (25 h / 23 h days) — go through UTC instead.</summary>
    public static double Seconds(DateTime start, DateTime end)
        => (end.ToUniversalTime() - start.ToUniversalTime()).TotalSeconds;

    public static string Dur(double seconds)
    {
        int total = (int)Math.Round(seconds);
        int h = total / 3600;
        int m = (total % 3600) / 60;
        if (h > 0 && m > 0) return $"{h}h {m}m";
        if (h > 0) return $"{h}h";
        return $"{m}m";
    }

    /// <summary>"1 blok", "2 bloka", "5 blokova", "21 blok", "12 blokova" — Croatian plural.</summary>
    public static string Blocks(int n)
    {
        int last = n % 10, lastTwo = n % 100;
        if (last == 1 && lastTwo != 11) return $"{n} blok";
        if (last is >= 2 and <= 4 && lastTwo is not (>= 12 and <= 14)) return $"{n} bloka";
        return $"{n} blokova";
    }

    /// <summary>A hand-typed time: "9", "930", "0930", "9:30", "9.30" or "9,30".</summary>
    public static (int Hour, int Minute)? ParseTime(string input)
    {
        string s = input.Trim().Replace('.', ':').Replace(',', ':');
        int h, m;
        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length != 2 || parts[0].Length is < 1 or > 2 || parts[1].Length != 2) return null;
            if (!parts[0].All(char.IsAsciiDigit) || !parts[1].All(char.IsAsciiDigit)) return null;
            h = int.Parse(parts[0], Invariant);
            m = int.Parse(parts[1], Invariant);
        }
        else
        {
            if (s.Length is < 1 or > 4 || !s.All(char.IsAsciiDigit)) return null;
            if (s.Length <= 2) { h = int.Parse(s, Invariant); m = 0; }
            else { h = int.Parse(s[..^2], Invariant); m = int.Parse(s[^2..], Invariant); }
        }
        if (h is < 0 or > 23 || m is < 0 or > 59) return null;
        return (h, m);
    }

    /// <summary>The same day as <paramref name="day"/>, at the given hour and minute.</summary>
    public static DateTime At(DateTime day, int hour, int minute) => day.Date.AddHours(hour).AddMinutes(minute);

    /// <summary>The longest entry allowed to cross midnight when the end is typed "before" the
    /// start — 23:30–00:30 is an entry over midnight, 10:00–9:00 a typo (not a 23-hour entry).</summary>
    public static readonly TimeSpan MaxOvernightSpan = TimeSpan.FromHours(12);

    /// <summary>Interprets the entry editor's typed times ("9:30") for the summary day
    /// <paramref name="day"/>:
    /// a time that didn't change (same HH:mm as in <paramref name="original"/>) returns the
    /// original instant, seconds included — otherwise fixing only the description would shift the
    /// entry by a few seconds (merging blocks and trimming neighbours);
    /// an entry already past midnight (in the previous day's file) is still read past midnight;
    /// an end that isn't after the start is the next day (…–00:00, 23:30–00:30), up to
    /// <see cref="MaxOvernightSpan"/>.</summary>
    public static (DateTime? Start, DateTime? End) EditRange(
        string startText, string endText, DateTime day, (DateTime Start, DateTime End)? original)
    {
        var dayStart = day.Date;
        var nextDay = dayStart.AddDays(1);
        static bool Unchanged((int Hour, int Minute) t, DateTime date) => date.Hour == t.Hour && date.Minute == t.Minute;

        DateTime? start = null;
        if (ParseTime(startText) is { } st)
        {
            if (original is { } o && Unchanged(st, o.Start))
                start = o.Start;
            else
            {
                var sameDay = At(dayStart, st.Hour, st.Minute);
                // An entry reaching past midnight: whichever of the two candidates is closer to
                // the original start (00:10 for a 23:30–00:30 entry is the next day, 23:45 the same).
                if (original is { } orig && orig.End > nextDay)
                {
                    var following = At(nextDay, st.Hour, st.Minute);
                    start = Math.Abs((following - orig.Start).TotalSeconds) < Math.Abs((sameDay - orig.Start).TotalSeconds)
                        ? following : sameDay;
                }
                else
                    start = sameDay;
            }
        }

        DateTime? end = null;
        if (ParseTime(endText) is { } et)
        {
            if (original is { } o && Unchanged(et, o.End))
                end = o.End;
            else
            {
                var baseDay = (start ?? dayStart).Date;
                var e = At(baseDay, et.Hour, et.Minute);
                if (start is DateTime s && e <= s)
                {
                    var rolled = At(baseDay.AddDays(1), et.Hour, et.Minute);
                    if (rolled - s <= MaxOvernightSpan) e = rolled;
                }
                end = e;
            }
        }
        return (start, end);
    }

    public static string Countdown(double seconds)
    {
        int total = Math.Max(0, (int)Math.Round(seconds));
        return $"{total / 60:D2}:{total % 60:D2}";
    }
}
