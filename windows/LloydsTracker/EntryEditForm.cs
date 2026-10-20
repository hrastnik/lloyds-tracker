using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LloydsTracker;

/// <summary>Što se uređuje u kronološkom pregledu — postojeći red ili novi unos. Vremena se
/// drže kao upisani tekst ("9:30"), a tumače tek kod provjere i spremanja. Zrcali macOS
/// EntryDraft.</summary>
internal sealed class EntryDraft
{
    /// <summary>Postojeći red; null = novi unos.</summary>
    public ChronoRow? Row { get; init; }
    /// <summary>Novi unos: rupa iz koje je pokrenut (null = gumb "Dodaj unos").</summary>
    public DateTime? GapStart { get; init; }
    public string Text { get; set; } = "";
    public string Start { get; set; } = "";
    public string End { get; set; } = "";
    public EntryKind Kind { get; set; } = EntryKind.Work;
    /// <summary>Točna vremena s kojima je uređivač otvoren (sa sekundama) — neizmijenjeno polje
    /// vraća njih, a ne punu minutu (vidi <see cref="Fmt.EditRange"/>).</summary>
    public DateTime OriginalStart { get; init; }
    public DateTime OriginalEnd { get; init; }

    public static EntryDraft Edit(ChronoRow row) => new()
    {
        Row = row, Text = row.Text, Start = Fmt.Hhmm(row.Start), End = Fmt.Hhmm(row.End), Kind = row.Kind,
        OriginalStart = row.Start, OriginalEnd = row.End,
    };

    public static EntryDraft New(DateTime start, DateTime end, DateTime? gapStart) => new()
    {
        GapStart = gapStart, Start = Fmt.Hhmm(start), End = Fmt.Hhmm(end),
        OriginalStart = start, OriginalEnd = end,
    };
}

/// <summary>Uređivač unosa unutar kronološkog pregleda — otvara se na mjestu reda (ispravak),
/// rupe ili gumba "Dodaj unos" (novi unos). Spojeni red (`2×`) se ispravlja kao jedna cjelina:
/// promjena samo opisa/vrste zadržava blokove, a promjena vremena ih stopi u jedan unos (novi
/// raspon nema stare granice). Preklopljeni dio drugih unosa se reže. Zrcali macOS
/// EntryEditor.</summary>
internal sealed class EntryEditor : Control
{
    private readonly EntryDraft _draft;
    private readonly DateTime _day;
    private readonly Func<IReadOnlyList<Entry>> _entries;

    public event Action<string, DateTime, DateTime, EntryKind>? SaveRequested;
    public event Action? DeleteRequested;
    public event Action? CancelRequested;

    private readonly PromptTextField _start;
    private readonly PromptTextField _end;
    private readonly PromptTextField _text;
    private readonly TrackedLabel _fromLabel;
    private readonly TrackedLabel _toLabel;
    private readonly Label _duration;
    private readonly FlatButton _work;
    private readonly FlatButton _pause;
    private readonly ComboBox? _history;
    private readonly Label _notice;
    private readonly FlatButton? _delete;
    private readonly FlatButton _cancel;
    private readonly FlatButton _save;

    private static readonly Color CardFill = Palette.White.OverBlack(0.06);
    private static readonly Color DeleteRed = Color.FromArgb(255, 92, 79);
    private const int Pad = 12;

    public EntryDraft Draft => _draft;

    public EntryEditor(EntryDraft draft, DateTime day, Func<IReadOnlyList<Entry>> entries, IReadOnlyList<string> history)
    {
        _draft = draft;
        _day = day;
        _entries = entries;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        // Container only — Tab goes straight to its fields and buttons.
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        BackColor = Palette.Black;

        _fromLabel = SectionLabel("OD");
        _toLabel = SectionLabel("DO");
        _start = TimeField(draft.Start, v => _draft.Start = v);
        _end = TimeField(draft.End, v => _draft.End = v);

        _duration = new Label { AutoSize = false, Font = Brand.Mono(9.5f, FontStyle.Bold), BackColor = CardFill, TextAlign = ContentAlignment.MiddleLeft, Size = new Size(Brand.S(64), Brand.S(20)) };
        Controls.Add(_duration);

        _work = KindPill("Rad", EntryKind.Work);
        _pause = KindPill("Pauza", EntryKind.Pause);

        _text = new PromptTextField(day, big: false, Palette.White.OverBlack(0.07), "Opis — npr. Projekt X — opis zadatka") { BackColor = CardFill };
        _text.Value = draft.Text;
        _text.ValueChanged += () => { _draft.Text = _text.Value; Sync(); };
        _text.SubmitRequested += Save;
        _text.EscapeRequested += () => CancelRequested?.Invoke();
        Controls.Add(_text);

        if (history.Count > 0)
        {
            // macOS ovdje ima Menu; WinForms nema borderless menu gumb, pa je to drop-down
            // koji se nakon odabira vrati na natpis.
            var items = new List<string> { "Povijest…" };
            items.AddRange(history);
            var combo = Dark.Combo(items.ToArray(), 0, Point.Empty, Brand.S(110));
            combo.DropDownWidth = Dark.DropDownWidthFor(combo, Brand.S(420));
            // Izbor ovdje nije vrijednost nego radnja (prepiše opis), pa vrijedi samo stavka
            // izabrana s otvorenog popisa (klik / Enter). Nad zatvorenim drop-downom strelice i
            // slova inače odmah mijenjaju izbor — ↓ ga zato samo otvori, ostalo ne radi ništa.
            combo.KeyDown += (_, e) =>
            {
                if (combo.DroppedDown || e.Alt) return;
                switch (e.KeyCode)
                {
                    case Keys.Down:
                        combo.DroppedDown = true;
                        e.Handled = true;
                        break;
                    case Keys.Up or Keys.Left or Keys.Right or Keys.PageUp or Keys.PageDown
                        or Keys.Home or Keys.End:
                        e.Handled = true;
                        break;
                }
            };
            combo.KeyPress += (_, e) => { if (!combo.DroppedDown) e.Handled = true; };
            combo.SelectionChangeCommitted += (_, _) =>
            {
                int i = combo.SelectedIndex;
                if (i > 0)
                {
                    _text.Value = items[i];
                    _text.FocusBox();
                }
                ResetHistory(combo);
            };
            // Esc / klik mimo popisa — natpis se vrati na "Povijest…" i kad je strelicama
            // prošao po popisu.
            combo.DropDownClosed += (_, _) => ResetHistory(combo);
            _history = combo;
            Controls.Add(_history);
        }

        _notice = new Label { AutoSize = false, Font = Brand.Ui(8.5f), BackColor = CardFill, TextAlign = ContentAlignment.TopLeft };
        Controls.Add(_notice);

        if (draft.Row is ChronoRow row)
        {
            _delete = new FlatButton
            {
                Text = row.IsMerged ? $"Obriši ({Fmt.Blocks(row.Ids.Count)})" : "Obriši",
                TextColor = DeleteRed, Fill = DeleteRed.With(0.12), CornerRadius = 8,
                Font = Brand.Ui(9f, FontStyle.Bold), BackColor = CardFill,
            };
            _delete.Size = new Size(FlatButton.TextWidth(_delete.Text, _delete.Font) + Brand.S(24), Brand.S(28));
            _delete.Click += (_, _) => DeleteRequested?.Invoke();
            Controls.Add(_delete);
        }

        _cancel = new FlatButton
        {
            Text = "Odustani", TextColor = Palette.Gray, BorderColor = Palette.White.OverBlack(0.2), BorderWidth = 1,
            CornerRadius = 8, Font = Brand.Ui(9f, FontStyle.Bold), BackColor = CardFill, Size = new Size(Brand.S(84), Brand.S(28)),
        };
        _cancel.Click += (_, _) => CancelRequested?.Invoke();
        Controls.Add(_cancel);

        _save = new FlatButton
        {
            Text = draft.Row == null ? "Dodaj" : "Spremi", TextColor = Palette.Black, Fill = Palette.Yellow,
            CornerRadius = 8, Font = Brand.Ui(9f, FontStyle.Bold), BackColor = CardFill, Size = new Size(Brand.S(84), Brand.S(28)),
        };
        _save.Click += (_, _) => Save();
        Controls.Add(_save);

        Height = Brand.S(Pad) + _start.Height + Brand.S(8) + _text.Height + Brand.S(6) + Brand.S(30) + Brand.S(6) + Brand.S(28) + Brand.S(Pad);
        Sync();
    }

    // MARK: - Building blocks

    private TrackedLabel SectionLabel(string title)
    {
        var label = new TrackedLabel { Text = title, Font = Brand.Ui(7.5f, FontStyle.Bold), ForeColor = Palette.Gray.With(0.6), Tracking = 1.2f, BackColor = CardFill };
        Controls.Add(label);
        return label;
    }

    /// <summary>Polje za vrijeme: upisuje se rukom ("930", "9:30"), a strelice ↑/↓ pomiču za
    /// 5 minuta. Napušteno polje se uredi u HH:mm.</summary>
    private PromptTextField TimeField(string value, Action<string> store)
    {
        var field = new PromptTextField(_day, big: false, Palette.White.OverBlack(0.07), "hh:mm") { BackColor = CardFill, Width = Brand.S(70) };
        field.UseTimeStyle();
        field.Value = value;
        field.ValueChanged += () => { store(field.Value); Sync(); };
        field.SubmitRequested += Save;
        field.EscapeRequested += () => CancelRequested?.Invoke();
        field.HistoryOlder += () => Step(field, 5);
        field.HistoryNewer += () => Step(field, -5);
        field.FocusLost += () =>
        {
            if (Fmt.ParseTime(field.Value) is { } t) field.Value = $"{t.Hour:D2}:{t.Minute:D2}";
        };
        Controls.Add(field);
        return field;
    }

    private static void Step(PromptTextField field, int minutes)
    {
        if (Fmt.ParseTime(field.Value) is not { } t) return;
        int total = Math.Clamp(t.Hour * 60 + t.Minute + minutes, 0, 23 * 60 + 59);
        field.Value = $"{total / 60:D2}:{total % 60:D2}";
    }

    /// <summary>Natpis "Povijest…" — tek nakon što combo dovrši svoje notifikacije (redoslijed
    /// odabira i zatvaranja popisa nije zajamčen), inače bi ga combo vratio na stavku.</summary>
    private static void ResetHistory(ComboBox combo) => combo.BeginInvoke(new Action(() =>
    {
        if (!combo.IsDisposed && combo.SelectedIndex != 0) combo.SelectedIndex = 0;
    }));

    private FlatButton KindPill(string title, EntryKind kind)
    {
        var pill = new FlatButton { Text = title, CornerRadius = 8, Font = Brand.Ui(9f, FontStyle.Bold), BackColor = CardFill, Size = new Size(Brand.S(64), Brand.S(28)) };
        pill.Click += (_, _) => { _draft.Kind = kind; Sync(); };
        Controls.Add(pill);
        return pill;
    }

    // MARK: - Layout

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_save == null) return;
        int pad = Brand.S(Pad);
        int right = Width - pad;

        int y = pad;
        int mid = y + _start.Height / 2;
        int x = pad;
        _fromLabel.Location = new Point(x, mid - _fromLabel.Height / 2);
        x = _fromLabel.Right + Brand.S(6);
        _start.Location = new Point(x, y);
        x = _start.Right + Brand.S(10);
        _toLabel.Location = new Point(x, mid - _toLabel.Height / 2);
        x = _toLabel.Right + Brand.S(6);
        _end.Location = new Point(x, y);
        _duration.Location = new Point(_end.Right + Brand.S(10), mid - _duration.Height / 2);
        _pause.Location = new Point(right - _pause.Width, mid - _pause.Height / 2);
        _work.Location = new Point(_pause.Left - Brand.S(4) - _work.Width, _pause.Top);

        y += _start.Height + Brand.S(8);
        int textRight = right;
        if (_history != null)
        {
            _history.Location = new Point(right - _history.Width, y + (_text.Height - _history.Height) / 2);
            textRight = _history.Left - Brand.S(8);
        }
        _text.SetBounds(pad, y, Math.Max(Brand.S(80), textRight - pad), _text.Height);

        y += _text.Height + Brand.S(6);
        _notice.SetBounds(pad, y, Width - pad * 2, Brand.S(30));

        y += Brand.S(30) + Brand.S(6);
        _save.Location = new Point(right - _save.Width, y);
        _cancel.Location = new Point(_save.Left - Brand.S(8) - _cancel.Width, y);
        if (_delete != null) _delete.Location = new Point(pad, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        using var path = Brand.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Brand.Sf(8));
        using (var b = new SolidBrush(CardFill)) g.FillPath(b, path);
        using var pen = new Pen(Palette.Yellow.With(0.5), 1f);
        g.DrawPath(pen, path);
    }

    // MARK: - State

    private (DateTime? Start, DateTime? End) Dates
        => Fmt.EditRange(_draft.Start, _draft.End, _day, (_draft.OriginalStart, _draft.OriginalEnd));
    private DateTime? StartDate => Dates.Start;
    private DateTime? EndDate => Dates.End;
    private bool RangeOk => StartDate is DateTime s && EndDate is DateTime e && e > s;
    /// <summary>Unos ne smije završiti u budućnosti — sljedeći prompt bi inače pitao za vrijeme
    /// koje je već "pokriveno" (i jedno od toga bi se izgubilo).</summary>
    private bool EndsInFuture => RangeOk && EndDate!.Value > DateTime.Now;
    private bool IsValid => _draft.Text.Trim().Length > 0 && RangeOk && !EndsInFuture;

    private bool TimesChanged
        => _draft.Row is not ChronoRow row || StartDate is not DateTime s || EndDate is not DateTime e
            || Math.Abs((s - row.Start).TotalSeconds) > 1 || Math.Abs((e - row.End).TotalSeconds) > 1;

    /// <summary>Unosi koje će spremanje skratiti ili maknuti.</summary>
    private int OverlapCount
        => RangeOk && TimesChanged
            ? Summarize.Overlapping(_entries(), StartDate!.Value, EndDate!.Value, _draft.Row?.Ids).Count
            : 0;

    /// <summary>Trajanje, upozorenja i stanje gumba nakon svake promjene — trajanje nakon
    /// izmjene je odmah vidljivo.</summary>
    private void Sync()
    {
        bool startOk = StartDate != null, endOk = EndDate != null;
        _start.BorderOverride = startOk ? null : Palette.Orange;
        _end.BorderOverride = endOk ? null : Palette.Orange;
        _start.Invalidate();
        _end.Invalidate();

        _duration.Text = startOk && endOk ? Fmt.Dur(Math.Max(0, Fmt.Seconds(StartDate!.Value, EndDate!.Value))) : "—";
        _duration.ForeColor = RangeOk ? Palette.Yellow : Palette.Orange;

        if (!startOk || !endOk)
        {
            _notice.Text = "Vrijeme upiši kao 9:30 ili 0930.";
            _notice.ForeColor = Palette.Orange;
        }
        else if (!RangeOk)
        {
            _notice.Text = "Kraj mora biti nakon početka.";
            _notice.ForeColor = Palette.Orange;
        }
        else if (EndsInFuture)
        {
            _notice.Text = $"Kraj ne može biti u budućnosti (sad je {Fmt.Hhmm(DateTime.Now)}).";
            _notice.ForeColor = Palette.Orange;
        }
        else
        {
            var parts = new List<string>();
            if (_draft.Row is ChronoRow row && row.IsMerged && TimesChanged)
                parts.Add($"Promjena vremena stapa {Fmt.Blocks(row.Ids.Count)} u jedan unos.");
            int n = OverlapCount;
            if (n > 0)
                parts.Add($"Preklapa se s {n} {(n == 1 ? "unosom" : "unosa")} — ovaj unos zamjenjuje preklopljeni dio.");
            if (EndDate!.Value.AddSeconds(-1).Date != StartDate!.Value.Date)
                parts.Add($"Završava iza ponoći ({Fmt.Hhmm(EndDate!.Value)} sutradan).");
            _notice.Text = string.Join(" ", parts);
            _notice.ForeColor = Palette.Yellow.OverBlack(0.85);
        }

        bool work = _draft.Kind == EntryKind.Work;
        StylePill(_work, work);
        StylePill(_pause, !work);

        _save.Fill = Palette.Yellow.With(IsValid ? 1 : 0.35);
        _save.Invalidate();
    }

    private static void StylePill(FlatButton pill, bool active)
    {
        pill.Fill = active ? Palette.Yellow : Color.Transparent;
        pill.TextColor = active ? Palette.Black : Palette.Gray;
        pill.BorderColor = active ? Color.Transparent : Palette.White.OverBlack(0.2);
        pill.BorderWidth = active ? 0 : 1;
        pill.Invalidate();
    }

    private void Save()
    {
        if (!IsValid) return;
        SaveRequested?.Invoke(_draft.Text.Trim(), StartDate!.Value, EndDate!.Value, _draft.Kind);
    }

    public void FocusText() => _text.FocusBox();
}
