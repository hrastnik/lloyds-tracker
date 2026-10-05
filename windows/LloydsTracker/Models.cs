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
        PromptStyle.Fullscreen => "Cijeli ekran (preko svega)",
        _ => style.ToString()
    };
}

/// <summary>Persisted settings — property order is alphabetical to match the
/// macOS encoder's sortedKeys output.</summary>
public sealed class AppSettings
{
    /// <summary>Automatic stop of tracking at a set time of day — so tracking never stays
    /// on overnight. A minute before, a warning pops up offering a same-day extension.</summary>
    [JsonPropertyName("autoStopEnabled")]
    public bool AutoStopEnabled { get; set; } = true;

    [JsonPropertyName("autoStopHour")]
    public int AutoStopHour { get; set; } = 16;

    [JsonPropertyName("autoStopMinute")]
    public int AutoStopMinute { get; set; } = 0;

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

    /// <summary>Chronological day view: adjacent entries with the same text (one ending
    /// where the next begins) are shown as a single entry.</summary>
    [JsonPropertyName("mergeAdjacentEntries")]
    public bool MergeAdjacentEntries { get; set; } = true;

    [JsonPropertyName("promptStyle")]
    public PromptStyle PromptStyle { get; set; } = PromptStyle.Floating;

    [JsonPropertyName("showStartupReminder")]
    public bool ShowStartupReminder { get; set; } = true;

    /// <summary>No reminder pops up on Saturday or Sunday (neither at the set time nor at app
    /// launch). Tracking can still be started manually on the weekend.</summary>
    [JsonPropertyName("skipWeekendReminders")]
    public bool SkipWeekendReminders { get; set; } = true;

    [JsonPropertyName("soundEnabled")]
    public bool SoundEnabled { get; set; } = true;

    /// <summary>The reminder also offers a start from the beginning of the work day —
    /// opening the laptop at 9:30 can then be recorded as work from 8:30 (the morning is
    /// backfilled).</summary>
    [JsonPropertyName("workdayStartBackfillEnabled")]
    public bool WorkdayStartBackfillEnabled { get; set; } = true;

    /// <summary>Reminder for the start of the work day — pops up at the set time, or, if the
    /// computer was asleep then, as soon as it wakes. A machine that's never shut down would
    /// otherwise get no reminder, since the launch one only fires when the computer boots.</summary>
    [JsonPropertyName("workdayStartEnabled")]
    public bool WorkdayStartEnabled { get; set; } = true;

    [JsonPropertyName("workdayStartHour")]
    public int WorkdayStartHour { get; set; } = 8;

    [JsonPropertyName("workdayStartMinute")]
    public int WorkdayStartMinute { get; set; } = 30;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>One block (or merged run of blocks) inside a prompt period, with its text.
/// An empty <paramref name="Text"/> means the block was skipped — it isn't recorded, it
/// goes back into the next prompt.</summary>
public readonly record struct PromptSegment(DateTime Start, DateTime End, string Text);

/// <summary>A period with no description — a skipped period carried forward.</summary>
public readonly record struct PromptSpan(DateTime Start, DateTime End)
{
    public double Duration => (End - Start).TotalSeconds;
}

public sealed class PromptRequest
{
    public readonly record struct PendingPause(DateTime Start, string Reason);

    /// <summary>Start/End are settable because the engine merges adjacent carried periods
    /// into the main one just before showing the prompt (see TrackerEngine.Show).</summary>
    public DateTime Start { get; set; }
    /// <summary>Fixed end of the period; null means "until the moment of answering".</summary>
    public DateTime? End { get; set; }
    /// <summary>Skipped periods that aren't adjacent to the main one (e.g. a pause in
    /// between) — shown as extra rows above it. Adjacent ones the engine merges in.</summary>
    public IReadOnlyList<PromptSpan> Carried { get; set; } = Array.Empty<PromptSpan>();
    public PendingPause? PauseAfter { get; init; }
    public bool IsFinal { get; init; }
    /// <summary>A manually triggered prompt ("Zapiši sada") — offers the "nastavljam s"
    /// field next to the period.</summary>
    public bool IsManual { get; init; }
    public string? Note { get; init; }
    public bool AllowSnooze { get; init; } = true;
}

/// <summary>The prompt's answer. Segments with no text were skipped.</summary>
public sealed class PromptResult
{
    public IReadOnlyList<PromptSegment> Segments { get; init; } = Array.Empty<PromptSegment>();
    /// <summary>Manual prompt: what the user continues with — becomes the next prompt's prefill.</summary>
    public string? NextUp { get; init; }
}

// MARK: - Daily summary (grouping)

public sealed class GroupSummary
{
    public string Text { get; set; } = "";
    public double Total { get; set; }
    public List<(DateTime Start, DateTime End)> Ranges { get; set; } = new();
}

/// <summary>One row of the chronological view — a single entry, or a run of merged
/// adjacent entries with the same text.</summary>
public sealed class ChronoRow
{
    public List<Guid> Ids { get; set; } = new();
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Text { get; set; } = "";
    public EntryKind Kind { get; set; }

    public double Duration => (End - Start).TotalSeconds;
    public bool IsMerged => Ids.Count > 1;
}

internal static class Summarize
{
    /// <summary>Chronological list of entries. With <paramref name="merging"/>, adjacent
    /// entries of the same text and kind — where one ends as the next begins — form a single
    /// row (14:45–15:00 + 15:00–15:15 → 14:45–15:15). Only immediate neighbours merge, so a
    /// pause or a different description breaks the run.</summary>
    public static List<ChronoRow> Chronology(IEnumerable<Entry> entries, bool merging)
    {
        var rows = new List<ChronoRow>();
        foreach (var e in entries.OrderBy(x => x.Start))
        {
            string text = e.Text.Trim();
            var last = rows.Count > 0 ? rows[^1] : null;
            if (merging && last != null && last.Kind == e.Kind && last.Text == text
                && Math.Abs((e.Start - last.End).TotalSeconds) <= 1)
            {
                last.Ids.Add(e.Id);
                last.End = Max(last.End, e.End);
            }
            else
            {
                rows.Add(new ChronoRow
                {
                    Ids = new List<Guid> { e.Id },
                    Start = e.Start,
                    End = e.End,
                    Text = text,
                    Kind = e.Kind,
                });
            }
        }
        return rows;
    }

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
