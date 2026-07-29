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
    private DateTime? _pausedSince;
    private readonly SessionMonitor _session = new();
    private readonly WinFormsTimer _timer;
    private readonly PromptController _prompt = new();
    private readonly StartupReminderController _startupReminder = new();
    private readonly AutoStopWarningController _autoStopWarning = new();
    private bool _autoStopWarningShown;
    /// <summary>How long before the automatic stop the warning pops up.</summary>
    private const double AutoStopLead = 60;
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
        _startupReminder.Show(Fmt.DayTitle(DateTime.Now), onStart: Start, onDismiss: () => { });
    }

    // MARK: - Controls

    public void Start()
    {
        var now = DateTime.Now;
        CurrentDayKey = Store.DayKey(now);
        Entries = Store.LoadDay(CurrentDayKey);
        _sessionStart = now;

        // Track from the start of the current interval (e.g. start at 9:56 with 15 min → from 9:45).
        // If an entry already exists past the interval start, begin at the current 5-min block
        // (never before the last entry's end) to avoid a duplicate record.
        var intervalStart = GridFloor(now, Interval);
        var coverFrom = intervalStart;
        if (Entries.Count > 0)
        {
            var latestEnd = Entries.Max(e => e.End);
            if (latestEnd > intervalStart)
                coverFrom = Max(GridFloor(now, 300), latestEnd);
        }
        _lastCovered = Min(coverFrom, now);
        PauseUntil = null;
        _pausedSince = null;
        AwaitingReturnSince = null;
        _activePromptEnd = null;
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
        if (!IsTracking) return;
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

        if ((periodEnd - periodStart).TotalSeconds > 60)
        {
            Show(new PromptRequest
            {
                Start = periodStart,
                End = periodEnd,
                IsFinal = true,
                Note = "Kraj dana — što si radio u zadnjem periodu?",
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
        if ((now - _lastCovered).TotalSeconds > 60)
        {
            Show(new PromptRequest
            {
                Start = _lastCovered,
                End = now,
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

    private void ShowAutoStopWarning(DateTime stopAt)
    {
        _autoStopWarningShown = true;
        if (Settings.SoundEnabled) SystemSounds.Asterisk.Play();
        _autoStopWarning.Show(stopAt, AutoStopLead,
            onExtend: ExtendAutoStop,
            onStopNow: Stop,
            onDismiss: () => { });
    }

    // MARK: - Tick loop

    private void Tick()
    {
        if (!IsTracking) return;
        var now = DateTime.Now;

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
        if (_pausedSince is DateTime since && now > since)
            Entries.Add(new Entry(Max(since, _lastCovered), now, "Pauza", EntryKind.Pause));
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
        if (Settings.SoundEnabled) SystemSounds.Asterisk.Play();
        _prompt.Show(
            request,
            Settings.PromptStyle,
            History,
            onSubmit: segments => HandleSubmit(request, segments),
            onSnooze: () => Snooze(5));
    }

    private void HandleSubmit(PromptRequest request, IReadOnlyList<PromptSegment> segments)
    {
        var now = DateTime.Now;
        // Extended end (if the prompt waited across boundaries) takes precedence over the original.
        var effectiveEnd = _activePromptEnd ?? request.End;
        _activePromptEnd = null;
        var coveredEnd = request.Start;
        foreach (var seg in segments)
        {
            if ((seg.End - seg.Start).TotalSeconds <= 5) continue;
            Entries.Add(new Entry(seg.Start, seg.End, seg.Text, EntryKind.Work));
            // Chronological order → last segment ends up as history[0] (prefill for the next prompt).
            PushHistory(seg.Text);
            coveredEnd = Max(coveredEnd, seg.End);
        }
        if (request.PauseAfter is PromptRequest.PendingPause pending && now > pending.Start)
        {
            Entries.Add(new Entry(pending.Start, now, pending.Reason, EntryKind.Pause));
            _lastCovered = Max(_lastCovered, now);
        }
        else
        {
            _lastCovered = Max(_lastCovered, effectiveEnd ?? coveredEnd);
        }
        PersistDay();

        if (request.IsFinal)
        {
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
