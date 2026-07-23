using System.Text.Json.Serialization;

namespace LloydsTracker;

public enum EntryKind
{
    Work,
    Pause
}

/// <summary>One recorded block of the day. Serialized to match the macOS JSON schema:
/// { id, start, end, text, kind } with ISO-8601 UTC timestamps.</summary>
public sealed class Entry
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonPropertyName("start")]
    public DateTime Start { get; set; }

    [JsonPropertyName("end")]
    public DateTime End { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("kind")]
    public EntryKind Kind { get; set; } = EntryKind.Work;

    [JsonIgnore]
    public double Duration => (End - Start).TotalSeconds;

    public Entry() { }

    public Entry(DateTime start, DateTime end, string text, EntryKind kind = EntryKind.Work)
    {
        Start = start;
        End = end;
        Text = text;
        Kind = kind;
    }
}

public enum PromptStyle
{
    Floating,
    Fullscreen
}

internal static class PromptStyleExtensions
{
    public static string Label(this PromptStyle style) => style switch
    {
        PromptStyle.Floating => "Floating panel (kut ekrana)",
        PromptStyle.Fullscreen => "Cijeli ekran (obavezan odgovor)",
        _ => style.ToString()
    };
}

/// <summary>Persisted settings — property order is alphabetical to match the
/// macOS encoder's sortedKeys output.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("historyLimit")]
    public int HistoryLimit { get; set; } = 15;

    /// <summary>Keyboard/mouse inactivity beyond the threshold → the period is recorded as a pause.</summary>
    [JsonPropertyName("idleDetectionEnabled")]
    public bool IdleDetectionEnabled { get; set; } = false;

    [JsonPropertyName("idleThresholdMinutes")]
    public int IdleThresholdMinutes { get; set; } = 5;

    [JsonPropertyName("intervalMinutes")]
    public int IntervalMinutes { get; set; } = 15;

    [JsonPropertyName("launchAtLogin")]
    public bool LaunchAtLogin { get; set; } = false;

    /// <summary>Locked screen → the absence period is recorded as a pause.</summary>
    [JsonPropertyName("lockPauseEnabled")]
    public bool LockPauseEnabled { get; set; } = false;

    [JsonPropertyName("promptStyle")]
    public PromptStyle PromptStyle { get; set; } = PromptStyle.Floating;

    [JsonPropertyName("showStartupReminder")]
    public bool ShowStartupReminder { get; set; } = true;

    [JsonPropertyName("soundEnabled")]
    public bool SoundEnabled { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>One block (or merged run of blocks) inside a prompt period, with its text.</summary>
public readonly record struct PromptSegment(DateTime Start, DateTime End, string Text);

public sealed class PromptRequest
{
    public readonly record struct PendingPause(DateTime Start, string Reason);

    public DateTime Start { get; init; }
    /// <summary>Fixed end of the period; null means "until the moment of answering".</summary>
    public DateTime? End { get; init; }
    public PendingPause? PauseAfter { get; init; }
    public bool IsFinal { get; init; }
    public string? Note { get; init; }
    public bool AllowSnooze { get; init; } = true;
}

// MARK: - Daily summary (grouping)

public sealed class GroupSummary
{
    public string Text { get; set; } = "";
    public double Total { get; set; }
    public List<(DateTime Start, DateTime End)> Ranges { get; set; } = new();
}

internal static class Summarize
{
    /// <summary>Groups work entries by text, merging adjacent ranges of the same text.</summary>
    public static List<GroupSummary> Groups(IEnumerable<Entry> entries)
    {
        var work = entries.Where(e => e.Kind == EntryKind.Work).OrderBy(e => e.Start).ToList();
        var byText = new Dictionary<string, GroupSummary>();
        var order = new List<string>();

        foreach (var e in work)
        {
            string key = e.Text.Trim();
            if (byText.TryGetValue(key, out var g))
            {
                if (g.Ranges.Count > 0)
                {
                    var last = g.Ranges[^1];
                    if ((e.Start - last.End).TotalSeconds < 90)
                        g.Ranges[^1] = (last.Start, Max(last.End, e.End));
                    else
                        g.Ranges.Add((e.Start, e.End));
                }
                else
                {
                    g.Ranges.Add((e.Start, e.End));
                }
                g.Total += e.Duration;
            }
            else
            {
                byText[key] = new GroupSummary
                {
                    Text = key,
                    Total = e.Duration,
                    Ranges = new List<(DateTime, DateTime)> { (e.Start, e.End) }
                };
                order.Add(key);
            }
        }

        return order.Select(k => byText[k]).OrderByDescending(g => g.Total).ToList();
    }

    public static double WorkTotal(IEnumerable<Entry> entries)
        => entries.Where(e => e.Kind == EntryKind.Work).Sum(e => e.Duration);

    public static double PauseTotal(IEnumerable<Entry> entries)
        => entries.Where(e => e.Kind == EntryKind.Pause).Sum(e => e.Duration);

    public static string ClipboardText(DateTime date, IReadOnlyList<Entry> entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("LLOYDS TRACKER — ").Append(Fmt.DayTitle(date)).Append('\n');
        sb.Append("Ukupno rad: ").Append(Fmt.Dur(WorkTotal(entries)));
        double pauses = PauseTotal(entries);
        if (pauses > 0) sb.Append(" · Pauze: ").Append(Fmt.Dur(pauses));
        sb.Append("\n\n");
        foreach (var g in Groups(entries))
        {
            sb.Append(Fmt.Dur(g.Total)).Append(" — ").Append(g.Text).Append('\n');
            string ranges = string.Join(" · ", g.Ranges.Select(r => $"{Fmt.Hhmm(r.Start)}–{Fmt.Hhmm(r.End)}"));
            sb.Append("    ").Append(ranges).Append('\n');
        }
        return sb.ToString();
    }

    public static string Csv(IReadOnlyList<Entry> entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("start,end,minutes,text,kind\n");
        foreach (var e in entries.OrderBy(x => x.Start))
        {
            string text = "\"" + e.Text.Replace("\"", "\"\"") + "\"";
            int minutes = (int)Math.Round(e.Duration / 60);
            sb.Append(Iso(e.Start)).Append(',')
              .Append(Iso(e.End)).Append(',')
              .Append(minutes).Append(',')
              .Append(text).Append(',')
              .Append(e.Kind == EntryKind.Work ? "work" : "pause").Append('\n');
        }
        return sb.ToString();
    }

    private static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;
}
