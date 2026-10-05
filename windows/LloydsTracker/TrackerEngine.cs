using System.Globalization;
using System.Media;
using System.Windows.Forms;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace LloydsTracker;

public enum TrayState { Idle, Tracking, Paused, AwaitingReturn }

/// <summary>State, timer, prompt logic, idle/pauses — the direct port of the macOS
/// TrackerEngine. Runs entirely on the UI thread (WinForms timer), so no locking is
/// needed, mirroring the original @MainActor actor isolation.</summary>
public sealed class TrackerEngine : IDisposable
{
    // MARK: - Observable-equivalent state
    public bool IsTracking { get; private set; }
    public List<Entry> Entries { get; private set; } = new();
    public List<string> History { get; private set; } = new();
    public DateTime? PauseUntil { get; private set; }
    public DateTime? NextPromptAt { get; private set; }
    public DateTime? AwaitingReturnSince { get; private set; }
    /// <summary>The auto-stop time for the current session (null = off). Extensions from the
    /// warning change only this, never the setting.</summary>
    public DateTime? AutoStopAt { get; private set; }
    public string? LaunchAtLoginStatus { get; private set; }
    public AppSettings Settings { get; private set; }

    /// <summary>Fired (UI thread) whenever state changes; the tray/popover re-read state.</summary>
    public event Action? Changed;

    public string CurrentDayKey { get; private set; }

    private DateTime? _sessionStart;
    private DateTime _lastCovered = DateTime.Now;
    /// <summary>End of the visible "regular" prompt's period; grows while it waits for an answer.
    /// null means there's no prompt that may be extended (e.g. pause / end-of-day).</summary>
    private DateTime? _activePromptEnd;
    /// <summary>Skipped periods that aren't adjacent to the next prompt (e.g. a pause in
    /// between) — carried forward as separate rows until they get filled in or the day ends.</summary>
    private readonly List<PromptSpan> _carriedSpans = new();
    /// <summary>The day is closed and the final prompt is still waiting for an answer — nothing
    /// more gets scheduled, and the day must not be closed a second time (a tick would otherwise
    /// loop the same prompt forever).</summary>
    private bool _awaitingFinalAnswer;
    private DateTime? _pausedSince;
    private readonly SessionMonitor _session = new();
    private readonly WinFormsTimer _timer;
    private readonly PromptController _prompt = new();
    private readonly StartupReminderController _startupReminder = new();
    private readonly AutoStopWarningController _autoStopWarning = new();
    private bool _autoStopWarningShown;
    /// <summary>How long before the automatic stop the warning pops up.</summary>
    private const double AutoStopLead = 60;
    /// <summary>The day (dayKey) whose work-day-start reminder is done with — shown, or the
    /// day got started meanwhile. Kept in memory: after an app restart the launch reminder
    /// takes over the role anyway.</summary>
    private string? _workdayReminderDayKey;
    /// <summary>Hidden control used to marshal background-thread callbacks (SessionSwitch)
    /// back onto the UI thread.</summary>
    private readonly Control _marshal = new();

    /// <summary>Set by the view layer — opens the "Pregled dana" window.</summary>
    public Action OpenSummary { get; set; } = () => { };

    public double Interval => Settings.IntervalMinutes * 60.0;
    public bool IsPromptVisible => _prompt.IsVisible;

    public TrackerEngine()
    {
        Settings = Store.LoadSettings();
        History = Store.LoadHistory();
        CurrentDayKey = Store.DayKey(DateTime.Now);
        Entries = Store.LoadDay(CurrentDayKey);

        _ = _marshal.Handle; // force handle creation on the UI thread
        _session.Changed += OnSessionChanged;

        _timer = new WinFormsTimer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // Short delay so the app settles (tray icon, screens) before the pop-up.
        var startupDelay = new WinFormsTimer { Interval = 1000 };
        startupDelay.Tick += (_, _) =>
        {
            startupDelay.Stop();
            startupDelay.Dispose();
            ShowStartupReminderIfNeeded();
        };
        startupDelay.Start();
    }

    /// <summary>Launch reminder — only if enabled and the day isn't started yet.</summary>
    public void ShowStartupReminderIfNeeded()
    {
        if (!Settings.ShowStartupReminder || IsTracking || _prompt.IsVisible) return;
        if (IsSkippedWeekend(DateTime.Now)) return;
        PresentStartReminder(DateTime.Now);
    }

    // MARK: - Controls

    /// <summary><paramref name="backfillFrom"/> (from the work-day-start reminder) moves the
    /// start of tracking back — the first prompt then asks about the whole morning, e.g.
    /// 8:30–9:45.</summary>
    public void Start(DateTime? backfillFrom = null)
    {
        var now = DateTime.Now;
        CurrentDayKey = Store.DayKey(now);
        Entries = Store.LoadDay(CurrentDayKey);
        _sessionStart = now;
        _workdayReminderDayKey = CurrentDayKey;
        _startupReminder.Close();

        // The backfill only counts inside today — a safety rail so the start can never land in
        // yesterday (the first prompt would then ask about a 25 h period).
        var backfill = backfillFrom;
        if (backfill != null && Store.DayKey(backfill.Value) != CurrentDayKey) backfill = null;

        // Track from the start of the current interval (e.g. start at 9:56 with 15 min → from 9:45).
        // If an entry already exists past that, begin at the last entry's end — when backfilling
        // so the rest of the morning gets filled in, otherwise at the current 5-min block (to
        // avoid a duplicate record).
        var coverFrom = backfill ?? GridFloor(now, Interval);
        if (Entries.Count > 0)
        {
            var latestEnd = Entries.Max(e => e.End);
            if (latestEnd > coverFrom)
                coverFrom = backfill == null ? Max(GridFloor(now, 300), latestEnd) : latestEnd;
        }
        _lastCovered = Min(coverFrom, now);
        PauseUntil = null;
        _pausedSince = null;
        AwaitingReturnSince = null;
        _activePromptEnd = null;
        _carriedSpans.Clear();
        _awaitingFinalAnswer = false;
        NextPromptAt = AlignedNextPrompt(now);
        _autoStopWarningShown = false;
        AutoStopAt = NextAutoStop(now);
        IsTracking = true;
        RaiseChanged();
    }

    public void Stop()
    {
        var now = DateTime.Now;
        // Stopping in the last minute before the auto-stop (from the warning or the tray menu)
        // still records the period up to the scheduled time — otherwise the day ends a minute
        // before the setting (e.g. 16:59:46 instead of 17:00).
        if (AutoStopAt is DateTime scheduled && scheduled > now && (scheduled - now).TotalSeconds <= AutoStopLead)
            Stop(scheduled);
        else
            Stop(now);
    }

    /// <summary><paramref name="endTime"/> is the end of the last period — for an automatic
    /// stop that's the scheduled time, not the moment the final prompt gets answered (which
    /// may well be the next morning).</summary>
    private void Stop(DateTime endTime)
    {
        if (!IsTracking || _awaitingFinalAnswer) return;
        var now = DateTime.Now;
        CancelAutoStop();
        if (PauseUntil != null) EndManualPause(endTime);
        _prompt.Close();
        _activePromptEnd = null;

        var periodStart = _lastCovered;
        var periodEnd = endTime;
        // If the user is away (idle / locked screen), record the absence as a pause and ask
        // only about the work up to the moment they left — otherwise the whole absence would
        // end up as "work".
        if (AwaitingReturnSince is DateTime gapStart)
        {
            AwaitingReturnSince = null;
            var pauseStart = Max(gapStart, periodStart);
            if (endTime > pauseStart)
                Entries.Add(new Entry(pauseStart, endTime, "Pauza (odsutnost)", EntryKind.Pause));
            periodEnd = pauseStart;
            _lastCovered = Max(_lastCovered, endTime);
        }

        bool hasPeriod = (periodEnd - periodStart).TotalSeconds > 60;
        if (hasPeriod || _carriedSpans.Count > 0)
        {
            // A prompt that stayed up overnight (laptop closed) gets answered tomorrow, while its
            // period was cut at the end of its own day — say so, or the entry looks wrong.
            bool overnight = Store.DayKey(endTime) != Store.DayKey(now);
            _awaitingFinalAnswer = true;
            Show(new PromptRequest
            {
                Start = periodStart,
                // A prompt hanging only because of skipped rows has no period of its own.
                End = hasPeriod ? periodEnd : periodStart,
                IsFinal = true,
                Note = overnight
                    ? $"Prompt je prenoćio — period je odrezan na kraj radnog dana ({Fmt.Hhmm(endTime)})."
                    : "Kraj dana — što si radio u zadnjem periodu?",
                AllowSnooze = false
            });
        }
        else
        {
            FinalizeStop();
        }
    }

    private void FinalizeStop()
    {
        IsTracking = false;
        _awaitingFinalAnswer = false;
        _carriedSpans.Clear();
        CancelAutoStop();
        NextPromptAt = null;
        _sessionStart = null;
        AwaitingReturnSince = null;
        PersistDay();
        RaiseChanged();
        OpenSummary();
    }

    public void Pause(int? minutes)
    {
        if (!IsTracking || PauseUntil != null) return;
        var now = DateTime.Now;
        _pausedSince = now;
        PauseUntil = minutes is int m ? now.AddMinutes(m) : DateTime.MaxValue;
        _prompt.Close();
        _activePromptEnd = null;
        bool hasPeriod = (now - _lastCovered).TotalSeconds > 60;
        if (hasPeriod || _carriedSpans.Count > 0)
        {
            Show(new PromptRequest
            {
                Start = _lastCovered,
                End = hasPeriod ? now : _lastCovered,
                Note = "Prije pauze — na čemu si radio?",
                AllowSnooze = false
            });
        }
        RaiseChanged();
    }

    public void Resume()
    {
        if (PauseUntil == null) return;
        EndManualPause(DateTime.Now);
    }

    /// <summary>A manually triggered prompt ("Zapiši sada") — asks about the period from the last
    /// record until now and offers the "nastavljam s" field, so the next prompt starts with that
    /// prefill. The prompting rhythm stays untouched: <see cref="NextPromptAt"/> doesn't move, and
    /// if the prompt outlives an interval boundary its period is simply extended (like a regular
    /// prompt's).</summary>
    public void ManualPrompt()
    {
        if (!CanPromptNow) return;
        if (_prompt.IsVisible)
        {
            _prompt.Focus();
            return;
        }
        var now = DateTime.Now;
        _activePromptEnd = now;
        Show(new PromptRequest
        {
            Start = _lastCovered,
            End = now,
            IsManual = true,
            Note = "Ručni zapis — spremi period do sada, pa (ako želiš) upiši čime nastavljaš.",
            AllowSnooze = false
        });
    }

    /// <summary>Whether "Zapiši sada" currently makes sense (the popover hides it otherwise).</summary>
    public bool CanPromptNow
        => IsTracking && !_awaitingFinalAnswer && PauseUntil == null && AwaitingReturnSince == null;

    public void Snooze(int minutes)
    {
        _prompt.Close();
        _activePromptEnd = null;
        // Snap to the 5-min grid so periods (and durations) stay aligned.
        var target = SnapToGrid(DateTime.Now.AddMinutes(minutes));
        NextPromptAt = Max(target, DateTime.Now.AddSeconds(60));
        RaiseChanged();
    }

    /// <summary>Nearest point on the 5-min grid (e.g. 10:17:40 → 10:20).</summary>
    private static DateTime SnapToGrid(DateTime date)
    {
        var hourStart = HourStart(date);
        const double step = 300;
        double offset = Math.Round((date - hourStart).TotalSeconds / step) * step;
        return hourStart.AddSeconds(offset);
    }

    /// <summary>Start of the grid block containing <paramref name="date"/> (e.g. 9:56 with step 900 → 9:45).</summary>
    private static DateTime GridFloor(DateTime date, double step)
    {
        var hourStart = HourStart(date);
        double elapsed = (date - hourStart).TotalSeconds;
        return hourStart.AddSeconds(Math.Floor(elapsed / step) * step);
    }

    /// <summary>Deletes one or more entries — a merged row in the chronological view covers
    /// several entries.</summary>
    public void DeleteEntries(IEnumerable<Guid> ids, string dayKey)
    {
        var set = ids.ToHashSet();
        if (dayKey == CurrentDayKey)
        {
            Entries.RemoveAll(e => set.Contains(e.Id));
            PersistDay();
            RaiseChanged();
        }
        else
        {
            var day = Store.LoadDay(dayKey);
            day.RemoveAll(e => set.Contains(e.Id));
            Store.SaveDay(dayKey, day);
            RaiseChanged();
        }
    }

    /// <summary>Corrects the entry (or entries) behind one row of the chronological view. If the
    /// times are unchanged, only the text and kind of every block in the row change (a merged row
    /// stays merged). If the times changed, the row becomes a <b>single</b> entry — the new range
    /// can't be split along the old block boundaries in any meaningful way.</summary>
    public void UpdateEntries(
        ChronoRow row, string dayKey, string text, DateTime start, DateTime end, EntryKind kind)
    {
        string clean = text.Trim();
        if (clean.Length == 0 || end <= start) return;
        var ids = row.Ids.ToHashSet();
        bool timesChanged = Math.Abs((start - row.Start).TotalSeconds) > 1
            || Math.Abs((end - row.End).TotalSeconds) > 1;

        void Apply(List<Entry> day)
        {
            if (timesChanged)
            {
                day.RemoveAll(e => ids.Contains(e.Id));
                day.Add(new Entry(start, end, clean, kind) { Id = row.Ids[0] });
            }
            else
            {
                foreach (var e in day.Where(e => ids.Contains(e.Id)))
                {
                    e.Text = clean;
                    e.Kind = kind;
                }
            }
            day.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        if (dayKey == CurrentDayKey)
        {
            Apply(Entries);
            Store.SaveDay(CurrentDayKey, Entries);
        }
        else
        {
            var day = Store.LoadDay(dayKey);
            Apply(day);
            Store.SaveDay(dayKey, day);
        }
        RaiseChanged();
    }

    /// <summary>Next prompt aligned to the hour (e.g. 15 min → :00, :15, :30, :45).
    /// An interval that doesn't divide the hour (20, 45) resets each full hour.</summary>
    private DateTime AlignedNextPrompt(DateTime date)
    {
        var hourStart = HourStart(date);
        double elapsed = (date - hourStart).TotalSeconds;
        var next = hourStart.AddSeconds((Math.Floor(elapsed / Interval) + 1) * Interval);
        var nextHour = hourStart.AddSeconds(3600);
        return Min(next, nextHour);
    }

    // MARK: - Automatic stop

    /// <summary>Next stop at the configured time of day; if that time has already passed
    /// today, it's scheduled for tomorrow (e.g. start at 20:00 with auto-stop 16:00).</summary>
    private DateTime? NextAutoStop(DateTime after)
    {
        if (!Settings.AutoStopEnabled) return null;
        int hour = Math.Clamp(Settings.AutoStopHour, 0, 23);
        int minute = Math.Clamp(Settings.AutoStopMinute, 0, 59);
        var target = new DateTime(after.Year, after.Month, after.Day, hour, minute, 0, after.Kind);
        return target > after ? target : target.AddDays(1);
    }

    private void CancelAutoStop()
    {
        AutoStopAt = null;
        _autoStopWarningShown = false;
        _autoStopWarning.Close();
    }

    /// <summary>Extend today's stop — counted from the scheduled time (16:00 + 30 → 16:30).
    /// The setting is untouched, so tomorrow the configured time applies again.</summary>
    public void ExtendAutoStop(int minutes)
    {
        if (AutoStopAt is not DateTime current) return;
        _autoStopWarning.Close();
        _autoStopWarningShown = false;
        AutoStopAt = Max(current, DateTime.Now).AddMinutes(minutes);
        RaiseChanged();
    }

    /// <summary>End of the work day for a session that stayed up overnight — null while the
    /// session is still inside its own day.
    ///
    /// The laptop gets closed with a prompt open and the answer comes tomorrow: without this rail
    /// the period would run all night and end up as a multi-hour entry dated yesterday. The cut is
    /// the time from the "Automatsko zaustavljanje" section on the session's day, and work already
    /// recorded past it (extensions from the warning, or the auto-stop being off) is respected.</summary>
    private DateTime? OvernightCutoff(DateTime now)
    {
        if (Store.DayKey(now) == CurrentDayKey) return null;
        // A session that legitimately crosses into the new day (start at 20:00 with auto-stop at
        // 16:00 → the stop is scheduled for tomorrow) isn't interrupted.
        if (Settings.AutoStopEnabled && AutoStopAt is DateTime stopAt && stopAt > now) return null;
        if (!DateTime.TryParseExact(CurrentDayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var sessionDay))
            return null;
        int hour = Math.Clamp(Settings.AutoStopHour, 0, 23);
        int minute = Math.Clamp(Settings.AutoStopMinute, 0, 59);
        var configured = new DateTime(
            sessionDay.Year, sessionDay.Month, sessionDay.Day, hour, minute, 0, DateTimeKind.Local);
        // Work recorded past the configured time too (auto-stop off, work after midnight) isn't
        // moved backwards — such a day closes at its own boundary, midnight.
        if (configured > _lastCovered) return configured;
        var dayEnd = new DateTime(
            sessionDay.Year, sessionDay.Month, sessionDay.Day, 0, 0, 0, DateTimeKind.Local).AddDays(1);
        return Max(dayEnd, _lastCovered);
    }

    private void ShowAutoStopWarning(DateTime stopAt)
    {
        _autoStopWarningShown = true;
        if (Settings.SoundEnabled) SystemSounds.Asterisk.Play();
        _autoStopWarning.Show(stopAt, AutoStopLead,
            onExtend: ExtendAutoStop,
            onStopNow: Stop,
            onDismiss: () => { });
    }

    // MARK: - Start of the work day

    /// <summary>Start of the work day on <paramref name="date"/> (null when the reminder is off).</summary>
    private DateTime? WorkdayStart(DateTime date)
    {
        if (!Settings.WorkdayStartEnabled) return null;
        int hour = Math.Clamp(Settings.WorkdayStartHour, 0, 23);
        int minute = Math.Clamp(Settings.WorkdayStartMinute, 0, 59);
        return new DateTime(date.Year, date.Month, date.Day, hour, minute, 0, date.Kind);
    }

    /// <summary>Saturday or Sunday with "Preskoči vikende" on — no reminder pops up then.
    /// Fixed Sat/Sun on every port (macOS deliberately doesn't use the region-dependent
    /// <c>isDateInWeekend</c>), so all three behave the same.</summary>
    private bool IsSkippedWeekend(DateTime date)
        => Settings.SkipWeekendReminders
            && date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>The time the reminder offers to backfill from ("Start od 8:30") — null when the
    /// option is off, when the work day hasn't started yet, or when the gap is too small for the
    /// backfill to differ from starting now.</summary>
    private DateTime? BackfillStart(DateTime now)
    {
        if (!Settings.WorkdayStartBackfillEnabled) return null;
        if (WorkdayStart(now) is not DateTime start) return null;
        return (now - start).TotalSeconds >= 300 ? start : null;
    }

    /// <summary>The reminder at the set time: pops up when the work day begins, or — if the
    /// computer was asleep then — as soon as it wakes and is unlocked (the timer keeps ticking
    /// after a resume, so the next tick catches it). Fires once a day.</summary>
    private void CheckWorkdayStart(DateTime now)
    {
        if (!Settings.WorkdayStartEnabled || IsTracking || _session.IsLocked) return;
        if (_prompt.IsVisible || IsSkippedWeekend(now)) return;
        if (WorkdayStart(now) is not DateTime start || now < start) return;

        // An open reminder waits for an answer as long as it takes, so it may be from yesterday
        // (it stayed up overnight) or from before the work day started, when the backfill wasn't
        // on offer yet. It carries a stale title and offer, and it also blocks today's reminder —
        // replace it with a fresh one; if it isn't stale, leave it alone.
        bool playSound = Settings.SoundEnabled;
        if (_startupReminder.IsVisible)
        {
            var shownAt = _startupReminder.ShownAt ?? DateTime.MinValue;
            bool fromPreviousDay = Store.DayKey(shownAt) != Store.DayKey(now);
            bool backfillAppeared = _startupReminder.ShownBackfillFrom == null && BackfillStart(now) != null;
            if (!fromPreviousDay && !backfillAppeared) return;
            // A window from today is already on screen and its sound has played — only its offer
            // changes, so it goes without a sound.
            if (!fromPreviousDay) playSound = false;
            _startupReminder.Close();
        }
        else if (_workdayReminderDayKey == Store.DayKey(now)) return;
        // Unlike the launch reminder, this one easily pops up while you're away from the screen
        // (e.g. the moment the laptop wakes), so it comes with a sound.
        if (playSound) SystemSounds.Asterisk.Play();
        PresentStartReminder(now);
    }

    /// <summary>Shared pop-up for both reminders (app launch and start of the work day).</summary>
    private void PresentStartReminder(DateTime now)
    {
        // A reminder shown before the work day starts doesn't use up today's slot — it still pops
        // up at the set time (e.g. launch at 7:00 with a work day starting at 8:30).
        if (WorkdayStart(now) is DateTime start && now >= start)
            _workdayReminderDayKey = Store.DayKey(now);
        _startupReminder.Show(Fmt.DayTitle(now), BackfillStart(now),
            // The backfill time is computed at click time, not at display time — otherwise a
            // reminder that stayed up overnight would start the day from yesterday's start.
            onStart: useBackfill => Start(useBackfill ? BackfillStart(DateTime.Now) : null),
            onDismiss: () => { });
    }

    // MARK: - Tick loop

    private void Tick()
    {
        var now = DateTime.Now;
        CheckWorkdayStart(now);
        // The day is closed and the final prompt is pending — nothing more gets scheduled.
        if (!IsTracking || _awaitingFinalAnswer) return;

        // Before anything else — the auto-stop applies while paused or awaiting a return too.
        if (Settings.AutoStopEnabled && AutoStopAt is DateTime stopAt)
        {
            if (now >= stopAt)
            {
                Stop(stopAt);
                return;
            }
            if (!_autoStopWarningShown && (stopAt - now).TotalSeconds <= AutoStopLead)
                ShowAutoStopWarning(stopAt);
        }

        // The safety rail for a session that stayed up overnight (laptop closed, often with a
        // prompt open): the period must not stretch into the new day.
        if (OvernightCutoff(now) is DateTime cutoff)
        {
            Stop(cutoff);
            return;
        }

        if (PauseUntil is DateTime until)
        {
            if (now >= until) EndManualPause(now);
            return;
        }

        if (AwaitingReturnSince is DateTime gapStart)
        {
            if (!_session.IsLocked && IdleMonitor.IdleSeconds() < 5)
                HandleReturn(gapStart, now);
            return;
        }

        if (_prompt.IsVisible)
        {
            // An unanswered prompt outlived the interval boundary — instead of opening a
            // second prompt, extend the existing one's period (accumulated time).
            if (NextPromptAt is DateTime boundary && now >= boundary && _activePromptEnd != null)
                ExtendActivePrompt(boundary);
            return;
        }
        if (NextPromptAt is DateTime next && now >= next)
            AttemptPrompt(now);
    }

    private void AttemptPrompt(DateTime now)
    {
        double idle = IdleMonitor.IdleSeconds();
        if (Settings.LockPauseEnabled && _session.IsLocked)
        {
            AwaitingReturnSince = Max(_session.LockedAt ?? now, _lastCovered);
            RaiseChanged();
        }
        else if (Settings.IdleDetectionEnabled && idle >= Settings.IdleThresholdMinutes * 60.0)
        {
            AwaitingReturnSince = Max(now.AddSeconds(-idle), _lastCovered);
            RaiseChanged();
        }
        else
        {
            // The period's end is the scheduled (aligned) prompt time, not the moment of
            // answering — entries stay exactly on the 5-min grid and late answers spill
            // into the next period.
            var end = NextPromptAt ?? now;
            _activePromptEnd = end;
            Show(new PromptRequest
            {
                Start = _lastCovered,
                End = end,
                AllowSnooze = Settings.PromptStyle == PromptStyle.Floating
            });
            // Next boundary at which this prompt extends (instead of opening a new one).
            NextPromptAt = AlignedNextPrompt(end);
        }
    }

    /// <summary>Extend the visible prompt to the next interval boundary and advance the next one.</summary>
    private void ExtendActivePrompt(DateTime boundary)
    {
        _activePromptEnd = boundary;
        _prompt.Extend(boundary);
        NextPromptAt = AlignedNextPrompt(boundary);
        RaiseChanged();
    }

    private void HandleReturn(DateTime gapStart, DateTime now)
    {
        AwaitingReturnSince = null;
        int gapMinutes = Math.Max(1, (int)((now - gapStart).TotalSeconds / 60));

        if ((gapStart - _lastCovered).TotalSeconds > 60)
        {
            Show(new PromptRequest
            {
                Start = _lastCovered,
                End = gapStart,
                PauseAfter = new PromptRequest.PendingPause(gapStart, "Pauza (odsutnost)"),
                Note = $"Bio si odsutan ~{gapMinutes} min — to razdoblje bit će označeno kao pauza.",
                AllowSnooze = false
            });
        }
        else
        {
            if (now > gapStart)
            {
                Entries.Add(new Entry(Max(gapStart, _lastCovered), now, "Pauza (odsutnost)", EntryKind.Pause));
                PersistDay();
            }
            _lastCovered = now;
            NextPromptAt = AlignedNextPrompt(now);
        }
        RaiseChanged();
    }

    private void EndManualPause(DateTime now)
    {
        if (_pausedSince is DateTime since)
        {
            // If the prompt before the pause was skipped, that period isn't adjacent to whatever
            // comes next (the pause sits between) — it gets carried forward as a separate row.
            Carry(new PromptSpan(_lastCovered, since));
            if (now > since)
                Entries.Add(new Entry(Max(since, _lastCovered), now, "Pauza", EntryKind.Pause));
        }
        _pausedSince = null;
        PauseUntil = null;
        _lastCovered = Max(_lastCovered, now);
        NextPromptAt = AlignedNextPrompt(now);
        PersistDay();
        RaiseChanged();
    }

    // MARK: - Prompt

    private void Show(PromptRequest request)
    {
        // Skipped periods come along with every prompt. Those adjacent to the main period merge
        // into it (one period that can be split with `✂`), the rest are shown as separate rows
        // above it.
        var carried = new List<PromptSpan>(_carriedSpans);
        while (carried.Count > 0 && Math.Abs((request.Start - carried[^1].End).TotalSeconds) <= 1)
        {
            var last = carried[^1];
            request.Start = last.Start;
            // A prompt with no period of its own (only skipped rows) takes the merged period's
            // end — otherwise it would stay degenerate and that time would be lost.
            request.End = Max(request.End ?? last.End, last.End);
            carried.RemoveAt(carried.Count - 1);
        }
        request.Carried = carried;

        if (Settings.SoundEnabled) SystemSounds.Asterisk.Play();
        _prompt.Show(
            request,
            Settings.PromptStyle,
            History,
            onSubmit: result => HandleSubmit(request, result),
            onSnooze: () => Snooze(5));
    }

    /// <summary>Adds a skipped period to the list carried into the next prompt; adjacent ones merge.</summary>
    private void Carry(PromptSpan span)
    {
        if (span.Duration <= 60) return;
        if (_carriedSpans.Count > 0 && Math.Abs((span.Start - _carriedSpans[^1].End).TotalSeconds) <= 1)
        {
            var last = _carriedSpans[^1];
            _carriedSpans[^1] = new PromptSpan(last.Start, Max(last.End, span.End));
        }
        else
        {
            _carriedSpans.Add(span);
        }
    }

    /// <summary>A segment with no text was <b>skipped</b> — it isn't recorded. If it sits at the end
    /// of the period, <c>_lastCovered</c> stays where it was, so the same period pops up in the next
    /// prompt (extended by a new interval). A skipped period followed by recorded time is carried
    /// forward as a separate row.</summary>
    private void HandleSubmit(PromptRequest request, PromptResult result)
    {
        var now = DateTime.Now;
        // Extended end (if the prompt waited across boundaries) takes precedence over the original.
        var effectiveEnd = _activePromptEnd ?? request.End;
        _activePromptEnd = null;
        // Every skipped period shown came back in the answer — the list is rebuilt from scratch.
        _carriedSpans.Clear();

        var segments = result.Segments.OrderBy(s => s.Start).ToList();
        var texts = segments.Select(s => s.Text.Trim()).ToList();

        // A tail of empty segments in the main period just "uncovers" time backwards.
        var coveredEnd = effectiveEnd ?? (segments.Count > 0 ? segments[^1].End : request.Start);
        int lastFilled = segments.Count - 1;
        while (lastFilled >= 0 && texts[lastFilled].Length == 0
               && segments[lastFilled].Start >= request.Start)
        {
            coveredEnd = segments[lastFilled].Start;
            lastFilled--;
        }

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if ((seg.End - seg.Start).TotalSeconds <= 5) continue;
            if (texts[i].Length == 0)
            {
                if (i <= lastFilled) Carry(new PromptSpan(seg.Start, seg.End));
            }
            else
            {
                Entries.Add(new Entry(seg.Start, seg.End, texts[i], EntryKind.Work));
                // Chronological order → last segment ends up as history[0] (prefill for the next prompt).
                PushHistory(texts[i]);
            }
        }
        // "Nastavljam s" from a manual prompt isn't recorded as an entry, it only goes into the
        // history — which makes it the next prompt's prefill.
        if (result.NextUp?.Trim() is string nextUp && nextUp.Length > 0) PushHistory(nextUp);

        if (request.PauseAfter is PromptRequest.PendingPause pending && now > pending.Start)
        {
            // The absence is recorded as a pause; skipped work before it isn't adjacent to what
            // follows, so it gets carried forward separately.
            Carry(new PromptSpan(coveredEnd, Min(pending.Start, effectiveEnd ?? pending.Start)));
            Entries.Add(new Entry(pending.Start, now, pending.Reason, EntryKind.Pause));
            _lastCovered = Max(_lastCovered, now);
        }
        else
        {
            _lastCovered = Max(_lastCovered, coveredEnd);
        }
        PersistDay();

        if (request.IsFinal)
        {
            // The day is closed — there's no later prompt to ask about skipped periods.
            FinalizeStop();
        }
        else if (PauseUntil == null)
        {
            NextPromptAt = AlignedNextPrompt(Max(now, _lastCovered));
            RaiseChanged();
        }
    }

    private void PushHistory(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return;
        History.RemoveAll(h => h == t);
        History.Insert(0, t);
        if (History.Count > Settings.HistoryLimit)
            History = History.Take(Settings.HistoryLimit).ToList();
        Store.SaveHistory(History);
    }

    private void PersistDay()
    {
        Entries.Sort((a, b) => a.Start.CompareTo(b.Start));
        Store.SaveDay(CurrentDayKey, Entries);
    }

    // MARK: - Settings

    /// <summary>Mutate settings and persist — mirrors the Swift `settings` didSet.</summary>
    public void MutateSettings(Action<AppSettings> mutate)
    {
        var old = Settings.Clone();
        mutate(Settings);
        Store.SaveSettings(Settings);
        SettingsChanged(old);
        RaiseChanged();
    }

    private void SettingsChanged(AppSettings old)
    {
        if (old.IntervalMinutes != Settings.IntervalMinutes && IsTracking && PauseUntil == null)
            NextPromptAt = AlignedNextPrompt(DateTime.Now);

        if (old.HistoryLimit != Settings.HistoryLimit && History.Count > Settings.HistoryLimit)
        {
            History = History.Take(Settings.HistoryLimit).ToList();
            Store.SaveHistory(History);
        }

        if (old.LaunchAtLogin != Settings.LaunchAtLogin)
            LaunchAtLoginStatus = LaunchAtLogin.Apply(Settings.LaunchAtLogin);

        // Changing the time / toggle drops any extension granted for today.
        if (old.AutoStopEnabled != Settings.AutoStopEnabled
            || old.AutoStopHour != Settings.AutoStopHour
            || old.AutoStopMinute != Settings.AutoStopMinute)
        {
            _autoStopWarning.Close();
            _autoStopWarningShown = false;
            AutoStopAt = IsTracking ? NextAutoStop(DateTime.Now) : null;
        }

        if (old.WorkdayStartEnabled != Settings.WorkdayStartEnabled
            || old.WorkdayStartHour != Settings.WorkdayStartHour
            || old.WorkdayStartMinute != Settings.WorkdayStartMinute
            || old.SkipWeekendReminders != Settings.SkipWeekendReminders)
        {
            // The new time applies from the next start of the work day: if today's has already
            // passed, it stays quiet today — otherwise the pop-up would appear the moment an
            // earlier hour gets dialled in the settings (or "Preskoči vikende" is turned off on
            // a weekend).
            var now = DateTime.Now;
            bool started = WorkdayStart(now) is DateTime start && now >= start;
            _workdayReminderDayKey = started ? Store.DayKey(now) : null;
        }
    }

    // MARK: - UI helpers

    public TrayState State
    {
        get
        {
            if (!IsTracking) return TrayState.Idle;
            if (PauseUntil != null) return TrayState.Paused;
            if (AwaitingReturnSince != null) return TrayState.AwaitingReturn;
            return TrayState.Tracking;
        }
    }

    public string StatusText
    {
        get
        {
            if (!IsTracking) return "Nije pokrenuto";
            if (PauseUntil is DateTime until)
                return until == DateTime.MaxValue ? "Pauzirano do nastavka" : $"Pauzirano do {Fmt.Hhmm(until)}";
            if (AwaitingReturnSince is DateTime since)
                return $"Odsutan od {Fmt.Hhmm(since)} — čekam povratak";
            return "Trackam";
        }
    }

    /// <summary>"Auto-stop u 16:00" — null when off or when tracking isn't running.</summary>
    public string? AutoStopText
    {
        get
        {
            if (!IsTracking || AutoStopAt is not DateTime at) return null;
            bool today = at.Date == DateTime.Now.Date;
            return today ? $"Auto-stop u {Fmt.Hhmm(at)}" : $"Auto-stop sutra u {Fmt.Hhmm(at)}";
        }
    }

    /// <summary>SessionSwitch may arrive on a background thread — hop to the UI thread
    /// before touching any UI-observing state.</summary>
    private void OnSessionChanged()
    {
        if (_marshal.IsHandleCreated && _marshal.InvokeRequired)
            _marshal.BeginInvoke(new Action(() => Changed?.Invoke()));
        else
            Changed?.Invoke();
    }

    private void RaiseChanged() => Changed?.Invoke();

    private static DateTime HourStart(DateTime d) => new(d.Year, d.Month, d.Day, d.Hour, 0, 0, d.Kind);
    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _session.Dispose();
        _prompt.Close();
        _autoStopWarning.Close();
        _marshal.Dispose();
    }
}
