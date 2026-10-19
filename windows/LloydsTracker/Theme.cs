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
    public static Color OverBlack(this Color c, double alpha)
    {
        double a = Math.Clamp(alpha, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(c.R * a + Black.R * (1 - a)),
            (int)Math.Round(c.G * a + Black.G * (1 - a)),
            (int)Math.Round(c.B * a + Black.B * (1 - a)));
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

    public static string Dur(double seconds)
    {
        int total = (int)Math.Round(seconds);
        int h = total / 3600;
        int m = (total % 3600) / 60;
        if (h > 0 && m > 0) return $"{h}h {m}m";
        if (h > 0) return $"{h}h";
        return $"{m}m";
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

    public static string Countdown(double seconds)
    {
        int total = Math.Max(0, (int)Math.Round(seconds));
        return $"{total / 60:D2}:{total % 60:D2}";
    }
}
