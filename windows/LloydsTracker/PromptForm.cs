using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LloydsTracker;

/// <summary>NSPanel (floating) / NSWindow (fullscreen) equivalent — owns the prompt form
/// and forwards submit/snooze, closing itself first (like the macOS PromptController).</summary>
internal sealed class PromptController
{
    private PromptForm? _form;

    public bool IsVisible => _form is { IsDisposed: false };

    public void Show(
        PromptRequest request,
        PromptStyle style,
        IReadOnlyList<string> history,
        Action<PromptResult> onSubmit,
        Action onSnooze)
    {
        if (IsVisible) return;

        var form = new PromptForm(
            request, style, history,
            onSubmit: result => { Close(); onSubmit(result); },
            onSnooze: () => { Close(); onSnooze(); });
        _form = form;
        form.FormClosed += (_, _) => { if (_form == form) _form = null; };
        form.Appear();
    }

    /// <summary>Produži vidljivi prompt do nove granice (skupno vrijeme).</summary>
    public void Extend(DateTime end)
    {
        if (_form is { IsDisposed: false } f) f.Extend(end);
    }

    /// <summary>Vraća već otvoreni prompt u prvi plan — "Zapiši sada" dok prompt visi.</summary>
    public void Focus()
    {
        if (_form is { IsDisposed: false } f)
        {
            f.Activate();
            f.FocusInitialField();
        }
    }

    public void Close()
    {
        if (_form is { IsDisposed: false } f)
        {
            _form = null;
            f.Close();
            f.Dispose();
        }
        else
        {
            _form = null;
        }
    }
}

/// <summary>A single-line brand-styled text field with a rounded border that highlights on focus.</summary>
internal sealed class PromptTextField : Control
{
    private readonly TextBox _box;
    public DateTime Key { get; }

    public event Action? SubmitRequested;
    public event Action? HistoryOlder;
    public event Action? HistoryNewer;
    public event Action? EscapeRequested;
    public event Action<PromptTextField>? FocusChanged;
    public event Action? ValueChanged;

    private bool _focused;

    public PromptTextField(DateTime key, bool big, Color fieldFill, string placeholder = "")
    {
        Key = key;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Black;

        int pad = Brand.S(big ? 14 : 10);
        _fieldFill = fieldFill;
        _pad = pad;
        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Multiline = false,
            Font = Brand.Ui(big ? 15f : 12f),
            ForeColor = Palette.White,
            BackColor = fieldFill,
            PlaceholderText = placeholder,
        };
        _box.GotFocus += (_, _) => { _focused = true; Invalidate(); FocusChanged?.Invoke(this); };
        _box.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        _box.TextChanged += (_, _) => ValueChanged?.Invoke();
        _box.KeyDown += OnBoxKeyDown;
        Controls.Add(_box);

        Height = _box.Font.Height + pad * 2;
    }

    private readonly Color _fieldFill;
    private readonly int _pad;

    public string Value
    {
        get => _box.Text;
        set => _box.Text = value;
    }

    public void FocusBox()
    {
        _box.Focus();
        _box.SelectionStart = _box.Text.Length;
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter:
                SubmitRequested?.Invoke();
                e.SuppressKeyPress = true;
                e.Handled = true;
                break;
            case Keys.Up:
                HistoryOlder?.Invoke();
                e.SuppressKeyPress = true;
                e.Handled = true;
                break;
            case Keys.Down:
                HistoryNewer?.Invoke();
                e.SuppressKeyPress = true;
                e.Handled = true;
                break;
            case Keys.Escape:
                EscapeRequested?.Invoke();
                e.SuppressKeyPress = true;
                e.Handled = true;
                break;
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _box.SetBounds(_pad, (Height - _box.Font.Height) / 2, Math.Max(Brand.S(10), Width - _pad * 2), _box.Font.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Brand.RoundedRect(r, Brand.Sf(10));
        using (var b = new SolidBrush(_fieldFill)) g.FillPath(b, path);
        using var pen = new Pen(Palette.Yellow.With(_focused ? 0.8 : 0.25), 1f);
        g.DrawPath(pen, path);
    }
}

internal sealed class PromptForm : Form
{
    private readonly PromptRequest _request;
    private readonly IReadOnlyList<string> _history;
    private readonly Action<PromptResult> _onSubmit;
    private readonly Action _onSnooze;

    /// <summary>Ključ polja "nastavljam s" u <c>_texts</c> — dijeli ga s opisima segmenata, pa
    /// listanje povijesti (↑/↓) radi i tamo. Nikad se ne poklapa s pravim vremenom bloka.</summary>
    private static readonly DateTime NextUpKey = DateTime.MaxValue;

    private readonly DateTime _periodStart;
    /// <summary>Kraj perioda se uživo produžuje dok prompt čeka odgovor (skupno vrijeme).</summary>
    private DateTime _periodEnd;
    private List<DateTime> _boundaries;
    private readonly HashSet<DateTime> _splitPoints = new();
    private readonly Dictionary<DateTime, string> _texts = new();
    private readonly Dictionary<DateTime, string> _drafts = new();
    private readonly Dictionary<DateTime, int> _historyIndices = new();

    private readonly bool _big;
    private readonly int _pad;
    private readonly int _innerW;
    private readonly Color _interior;
    private readonly Color _fieldFill;
    private readonly Color _carriedFill;

    private FlowLayoutPanel _stack = null!;
    private FlowLayoutPanel _mainPanel = null!;
    private FlowLayoutPanel _segmentsPanel = null!;
    private Panel _hintsPanel = null!;
    private Panel _actionsPanel = null!;
    private BlockBarControl? _blockBar;
    private Label? _timeLabel;
    private TrackedLabel? _periodSectionLabel;
    private PromptTextField? _nextUpField;
    private CardPanel? _cardPanel;    // fullscreen only
    private Panel? _logoRow;          // fullscreen only
    private Label? _fullscreenHint;   // fullscreen only
    private readonly List<PromptTextField> _fields = new();
    private readonly List<PromptTextField> _carriedFields = new();

    private int _floatRight;
    private int _floatTop;

    private string Prefill => _history.Count > 0 ? _history[0] : "";

    /// <summary>Preskočeni periodi iz prijašnjih promptova — svoja skupina, iznad crte.</summary>
    private IReadOnlyList<PromptSpan> Carried => _request.Carried;

    /// <summary>Degeneriran glavni period (npr. ručni prompt odmah nakon odgovora, ili dan
    /// zatvoren točno na granici) skriva se ako ima preskočenih redova — inače ostaje kao
    /// jedini red.</summary>
    private bool ShowsMainPeriod => _periodEnd > _periodStart || Carried.Count == 0;

    /// <summary>Prompt se ne aktivira odmah — vidi <see cref="PanelFade"/>.</summary>
    protected override bool ShowWithoutActivation => true;

    public PromptForm(
        PromptRequest request,
        PromptStyle style,
        IReadOnlyList<string> history,
        Action<PromptResult> onSubmit,
        Action onSnooze)
    {
        _request = request;
        _history = history;
        _onSubmit = onSubmit;
        _onSnooze = onSnooze;

        _periodStart = request.Start;
        _periodEnd = Max(request.End ?? DateTime.Now, _periodStart);
        _boundaries = PromptGeometry.GridBoundaries(_periodStart, _periodEnd);
        // Pre-fill dobiva samo glavni period; preskočeni redovi ostaju prazni jer su svjesno
        // ostavljeni za kasnije — Enter ih ne smije napuniti zadnjim unosom.
        _texts[_periodStart] = Prefill;

        _big = style == PromptStyle.Fullscreen;
        _pad = Brand.S(_big ? 28 : 18);
        int cardWidth = Brand.S(_big ? 560 : 420);
        _innerW = cardWidth - _pad * 2;
        _interior = _big ? Palette.White.OverBlack(0.04) : Palette.Black;
        _fieldFill = Palette.White.OverBlack(0.07);
        _carriedFill = Palette.White.OverBlack(0.04);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Palette.Black;
        AutoScaleMode = AutoScaleMode.None;

        BuildStack();

        if (_big) BuildFullscreenChrome();
        else BuildFloatingChrome();
    }

    /// <summary>Fade-in na svoju poziciju; tipkovnicu preuzima (i polje fokusira) tek nakon
    /// <see cref="PanelFade.KeyDelayMs"/>. Preko cijelog ekrana pomak ne treba — samo fade.</summary>
    public void Appear()
        => PanelFade.Appear(this, slide: _big ? 0 : 10, onKeyboard: FocusInitialField);

    /// <summary>Fokus na prvi red prompta — preskočeni ako ih ima, inače glavni period.</summary>
    public void FocusInitialField()
    {
        if (IsDisposed) return;
        var first = _carriedFields.FirstOrDefault() ?? _fields.FirstOrDefault();
        if (first != null) FocusFieldAt(first.Key);
    }

    // MARK: - Card content (shared)

    private void BuildStack()
    {
        _stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = _interior,
            Padding = new Padding(_pad),
            Margin = Padding.Empty,
        };

        AddRow(BuildHeaderRow(), first: true);

        if (!string.IsNullOrEmpty(_request.Note))
            AddRow(WrappedLabel(_request.Note!, Brand.Ui(_big ? 10.5f : 9f), Palette.Yellow.With(0.9), _interior, _innerW));

        if (Carried.Count > 0)
            AddRow(BuildCarriedPanel());

        AddRow(BuildMainPanel());

        if (_request.IsManual)
            AddRow(BuildNextUpPanel());

        // Dva reda: gore tipkovnica, dolje akcije. U jednom redu se na 420 px natpisi lome.
        _hintsPanel = new Panel { Width = _innerW, BackColor = _interior, Margin = new Padding(0, Brand.S(14), 0, 0) };
        _stack.Controls.Add(_hintsPanel);
        _actionsPanel = new Panel { Width = _innerW, BackColor = _interior, Margin = new Padding(0, Brand.S(10), 0, 0) };
        _stack.Controls.Add(_actionsPanel);
        BuildActions();

        RebuildSegmentsUI();
    }

    private void AddRow(Control c, bool first = false)
    {
        c.Margin = new Padding(0, first ? 0 : Brand.S(14), 0, 0);
        if (c.Width == 0) c.Width = _innerW;
        _stack.Controls.Add(c);
    }

    private Panel BuildHeaderRow()
    {
        var titleFont = Brand.Ui(_big ? 20f : 13f, FontStyle.Bold);
        int h = Math.Max(titleFont.Height, Brand.S(20));
        var row = new Panel { Width = _innerW, Height = h, BackColor = _interior };

        var dot = new Dot { Color = Palette.Yellow, BackColor = _interior, Size = new Size(Brand.S(9), Brand.S(9)) };
        dot.Location = new Point(0, (h - Brand.S(9)) / 2);
        row.Controls.Add(dot);

        var title = new TrackedLabel
        {
            Text = "NA ČEMU RADIŠ?",
            Font = titleFont,
            ForeColor = Palette.White,
            Tracking = 1.5f,
            BackColor = _interior,
        };
        title.Location = new Point(dot.Right + Brand.S(8), (h - title.Height) / 2);
        row.Controls.Add(title);

        var timeFont = Brand.Mono(_big ? 10.5f : 9f, FontStyle.Regular);
        var timeText = $"{Fmt.Hhmm(_periodStart)} – {Fmt.Hhmm(_periodEnd)}";
        var timeSize = TextRenderer.MeasureText(timeText, timeFont);
        _timeLabel = new Label
        {
            AutoSize = false,
            Text = timeText,
            Font = timeFont,
            ForeColor = Palette.Gray,
            BackColor = _interior,
            TextAlign = ContentAlignment.MiddleRight,
            Width = timeSize.Width + Brand.S(4),
            Height = h,
        };
        _timeLabel.Location = new Point(_innerW - _timeLabel.Width, 0);
        _timeLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        row.Controls.Add(_timeLabel);

        return row;
    }

    /// <summary>Preskočeni periodi iz prijašnjih promptova su svoja skupina, iznad crte —
    /// traka blokova (`✂`) i pre-fill vrijede samo za glavni period pod njom.</summary>
    private FlowLayoutPanel BuildCarriedPanel()
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = _interior,
            Width = _innerW,
        };
        panel.Controls.Add(SectionLabel("PRESKOČENO PRIJE — POPUNI ILI OSTAVI ZA KASNIJE"));

        foreach (var span in Carried.OrderBy(c => c.Start))
        {
            var field = new PromptTextField(span.Start, _big, _carriedFill, "preskočeno — upiši ili ostavi prazno");
            WireField(field);
            _carriedFields.Add(field);
            var row = TimedRow(field, span.Start, span.End, carried: true);
            row.Margin = new Padding(0, Brand.S(8), 0, 0);
            panel.Controls.Add(row);
        }

        var divider = new Panel
        {
            Width = _innerW,
            Height = Brand.S(1),
            BackColor = Palette.White.OverBlack(0.12),
            Margin = new Padding(0, Brand.S(14), 0, 0),
        };
        panel.Controls.Add(divider);
        return panel;
    }

    private FlowLayoutPanel BuildMainPanel()
    {
        _mainPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = _interior,
            Width = _innerW,
        };

        if (Carried.Count > 0)
        {
            _periodSectionLabel = SectionLabel(PeriodSectionText());
            _mainPanel.Controls.Add(_periodSectionLabel);
        }

        if (_boundaries.Count > 0)
        {
            _blockBar = NewBlockBar();
            _mainPanel.Controls.Add(_blockBar);
        }

        _segmentsPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = _interior,
            Margin = new Padding(0, Brand.S(14), 0, 0),
            Width = _innerW,
        };
        _mainPanel.Controls.Add(_segmentsPanel);
        return _mainPanel;
    }

    private string PeriodSectionText() => $"PERIOD {Fmt.Hhmm(_periodStart)}–{Fmt.Hhmm(_periodEnd)}";

    private BlockBarControl NewBlockBar()
    {
        var bar = new BlockBarControl
        {
            Width = _innerW,
            PeriodStart = _periodStart,
            PeriodEnd = _periodEnd,
            Boundaries = _boundaries,
            SplitPoints = _splitPoints,
            BackColor = _interior,
            Margin = new Padding(0, Brand.S(14), 0, 0),
        };
        bar.SplitToggled += ToggleSplit;
        bar.SegmentFocused += FocusFieldAt;
        return bar;
    }

    /// <summary>Ručni prompt: čime korisnik nastavlja. Ne bilježi se kao unos — samo
    /// pre-fillava sljedeći prompt, pa se prebacivanje na drugi projekt zapiše u jednom
    /// koraku.</summary>
    private FlowLayoutPanel BuildNextUpPanel()
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = _interior,
            Width = _innerW,
        };
        panel.Controls.Add(SectionLabel("NASTAVLJAM S — NIJE OBAVEZNO"));

        _nextUpField = new PromptTextField(NextUpKey, _big, Palette.White.OverBlack(0.05), "npr. Projekt B — hitni fix");
        _nextUpField.Width = _innerW;
        _nextUpField.Margin = new Padding(0, Brand.S(6), 0, 0);
        WireField(_nextUpField);
        panel.Controls.Add(_nextUpField);

        var hint = new Label
        {
            AutoSize = true,
            Text = "Sljedeći prompt kreće s ovim opisom.",
            Font = Brand.Ui(8f),
            ForeColor = Palette.Gray.With(0.5),
            BackColor = _interior,
            Margin = new Padding(0, Brand.S(6), 0, 0),
        };
        panel.Controls.Add(hint);
        return panel;
    }

    private TrackedLabel SectionLabel(string title) => new()
    {
        Text = title,
        Font = Brand.Ui(7f, FontStyle.Bold),
        ForeColor = Palette.Gray.With(0.6),
        Tracking = 1.2f,
        BackColor = _interior,
    };

    // MARK: - Segment rows

    private List<(DateTime Start, DateTime End)> Segments()
        => PromptGeometry.Segments(_periodStart, _periodEnd, _boundaries, _splitPoints);

    private static Label WrappedLabel(string text, Font font, Color fore, Color back, int width)
    {
        var l = new Label { AutoSize = false, Text = text, Font = font, ForeColor = fore, BackColor = back, Width = width };
        l.Height = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 2;
        return l;
    }

    private void WireField(PromptTextField field)
    {
        field.SubmitRequested += Submit;
        field.EscapeRequested += Skip;
        field.HistoryOlder += () => CycleHistory(field, older: true);
        field.HistoryNewer += () => CycleHistory(field, older: false);
        field.FocusChanged += OnFieldFocused;
        field.Value = _texts.GetValueOrDefault(field.Key, "");
    }

    /// <summary>Red s vremenom lijevo i poljem desno. Preskočeni red nosi i `↩`.</summary>
    private Panel TimedRow(PromptTextField field, DateTime start, DateTime end, bool carried)
    {
        int labelW = Brand.S(_big ? 96 : 84);
        var rowPanel = new Panel
        {
            Width = _innerW,
            Height = field.Height,
            BackColor = _interior,
        };
        var label = new Label
        {
            AutoSize = false,
            Text = (carried ? "↩ " : "") + $"{Fmt.Hhmm(start)}–{Fmt.Hhmm(end)}",
            Font = Brand.Mono(_big ? 9.5f : 8f, FontStyle.Bold),
            ForeColor = carried ? Palette.Gray.With(0.55) : Palette.Gray,
            BackColor = _interior,
            Width = labelW,
            Height = field.Height,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(0, 0),
        };
        field.Width = _innerW - labelW - Brand.S(8);
        field.Location = new Point(labelW + Brand.S(8), 0);
        rowPanel.Controls.Add(label);
        rowPanel.Controls.Add(field);
        return rowPanel;
    }

    private void RebuildSegmentsUI()
    {
        SyncTextsFromFields();

        _segmentsPanel.SuspendLayout();
        foreach (Control c in _segmentsPanel.Controls.Cast<Control>().ToList()) c.Dispose();
        _fields.Clear();
        _segmentsPanel.Controls.Clear();

        var segs = Segments();
        bool single = segs.Count == 1;

        foreach (var seg in segs)
        {
            var field = new PromptTextField(seg.Start, _big, _fieldFill, "npr. Projekt X — opis zadatka");
            WireField(field);
            _fields.Add(field);

            if (single)
            {
                // Nerazbijen period nosi vrijeme u naslovu (ili na sekciji), pa se u redu
                // ne ponavlja.
                field.Width = _innerW;
                field.Margin = new Padding(0, _segmentsPanel.Controls.Count == 0 ? 0 : Brand.S(8), 0, 0);
                _segmentsPanel.Controls.Add(field);
            }
            else
            {
                var rowPanel = TimedRow(field, seg.Start, seg.End, carried: false);
                rowPanel.Margin = new Padding(0, _segmentsPanel.Controls.Count == 0 ? 0 : Brand.S(8), 0, 0);
                _segmentsPanel.Controls.Add(rowPanel);
            }
        }
        _segmentsPanel.ResumeLayout(true);

        if (_blockBar != null)
        {
            _blockBar.SingleSegment = single;
            _blockBar.Invalidate();
        }

        // Uz preskočene redove period piše na svojoj sekciji, da se ne čita kao da vrijedi
        // za cijeli prompt.
        _mainPanel.Visible = ShowsMainPeriod;
        if (_timeLabel != null) _timeLabel.Visible = ShowsMainPeriod && Carried.Count == 0;
        if (_periodSectionLabel != null) _periodSectionLabel.Text = PeriodSectionText();

        RebuildHints(single);
        Relayout();
    }

    private void OnFieldFocused(PromptTextField field)
    {
        if (_blockBar != null)
        {
            _blockBar.FocusedStart = field.Key;
            _blockBar.Invalidate();
        }
    }

    // MARK: - Hints row

    private void RebuildHints(bool single)
    {
        _hintsPanel.SuspendLayout();
        _hintsPanel.Controls.Clear();

        int x = 0;
        int chipH = 0;

        void AddHint(string key, string label)
        {
            var chip = new KeyChip(key, _interior, Palette.Gray);
            chip.Location = new Point(x, 0);
            _hintsPanel.Controls.Add(chip);
            chipH = Math.Max(chipH, chip.Height);
            x += chip.Width + Brand.S(4);

            var lbl = new Label
            {
                AutoSize = true,
                Text = label,
                Font = Brand.Ui(8f),
                ForeColor = Palette.Gray,
                BackColor = _interior,
            };
            lbl.Location = new Point(x, (chip.Height - lbl.Height) / 2);
            _hintsPanel.Controls.Add(lbl);
            x += lbl.Width + Brand.S(12);
        }

        AddHint("↑↓", "povijest");
        AddHint("Enter", "spremi");
        AddHint("Esc", "preskoči");
        if (single && ShowsMainPeriod && _boundaries.Count > 0)
            AddHint("✂", "razbij period");

        _hintsPanel.Height = Math.Max(chipH, Brand.S(16));
        _hintsPanel.ResumeLayout(true);
    }

    /// <summary>Odgoda + "Preskoči" — odgovor nije obavezan.</summary>
    private void BuildActions()
    {
        _actionsPanel.SuspendLayout();
        _actionsPanel.Controls.Clear();

        var skip = new FlatButton
        {
            Text = "Preskoči",
            TextColor = Palette.Gray,
            Fill = Color.Transparent,
            BorderColor = Palette.White.OverBlack(0.22),
            BorderWidth = 1,
            CornerRadius = 6,
            Font = Brand.Ui(9f, FontStyle.Bold),
            BackColor = _interior,
            Size = new Size(Brand.S(80), Brand.S(24)),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        skip.Location = new Point(_innerW - skip.Width, 0);
        skip.Click += (_, _) => Skip();
        _actionsPanel.Controls.Add(skip);
        _actionsPanel.Height = skip.Height;

        if (_request.AllowSnooze)
        {
            var snooze = new FlatButton
            {
                Text = "Odgodi 5 min",
                TextColor = Palette.Gray,
                Fill = Color.Transparent,
                Font = Brand.Ui(9f, FontStyle.Underline),
                Align = ContentAlignment.MiddleRight,
                BackColor = _interior,
                Height = skip.Height,
                Width = Brand.S(100),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            snooze.Location = new Point(skip.Left - Brand.S(10) - snooze.Width, 0);
            snooze.Click += (_, _) => _onSnooze();
            _actionsPanel.Controls.Add(snooze);
        }

        _actionsPanel.ResumeLayout(true);
    }

    // MARK: - Split toggling

    private void ToggleSplit(DateTime b)
    {
        SyncTextsFromFields();
        if (_splitPoints.Contains(b))
        {
            _splitPoints.Remove(b);
            var segs = Segments();
            DateTime target = _periodStart;
            foreach (var s in segs) if (s.Start <= b) target = s.Start;
            RebuildSegmentsUI();
            FocusFieldAt(target);
        }
        else
        {
            _splitPoints.Add(b);
            if (!_texts.ContainsKey(b)) _texts[b] = "";
            RebuildSegmentsUI();
            FocusFieldAt(b);
        }
    }

    // MARK: - Actions

    /// <summary>Odgovor nije obavezan: segmenti bez teksta su preskočeni i engine ih vraća u
    /// sljedeći prompt.</summary>
    private void Submit() => _onSubmit(Result(skipAll: false));

    private void Skip() => _onSubmit(Result(skipAll: true));

    private PromptResult Result(bool skipAll)
    {
        SyncTextsFromFields();
        var segments = new List<PromptSegment>();
        foreach (var span in Carried.OrderBy(c => c.Start))
            segments.Add(new PromptSegment(span.Start, span.End,
                skipAll ? "" : _texts.GetValueOrDefault(span.Start, "")));
        if (ShowsMainPeriod)
        {
            foreach (var seg in Segments())
                segments.Add(new PromptSegment(seg.Start, seg.End,
                    skipAll ? "" : _texts.GetValueOrDefault(seg.Start, "")));
        }
        return new PromptResult
        {
            Segments = segments,
            // "Nastavljam s" vrijedi i kad se period preskoči — prebacivanje na drugi
            // projekt je jedini razlog zašto je to polje tamo.
            NextUp = _request.IsManual ? _texts.GetValueOrDefault(NextUpKey, "") : null,
        };
    }

    private void CycleHistory(PromptTextField field, bool older)
    {
        if (_history.Count == 0) return;
        var key = field.Key;
        if (!_historyIndices.ContainsKey(key)) _drafts[key] = field.Value;
        int idx = _historyIndices.TryGetValue(key, out var v) ? v : -1;
        idx += older ? 1 : -1;
        if (idx < 0)
        {
            _historyIndices.Remove(key);
            field.Value = _drafts.GetValueOrDefault(key, "");
            _texts[key] = field.Value;
            return;
        }
        idx = Math.Min(idx, _history.Count - 1);
        _historyIndices[key] = idx;
        field.Value = _history[idx];
        _texts[key] = field.Value;
    }

    private void SyncTextsFromFields()
    {
        foreach (var f in _fields) _texts[f.Key] = f.Value;
        foreach (var f in _carriedFields) _texts[f.Key] = f.Value;
        if (_nextUpField is { IsDisposed: false } n) _texts[n.Key] = n.Value;
    }

    private void FocusFieldAt(DateTime start)
    {
        var field = _fields.FirstOrDefault(f => f.Key == start)
            ?? _carriedFields.FirstOrDefault(f => f.Key == start)
            ?? _fields.FirstOrDefault()
            ?? _carriedFields.FirstOrDefault();
        field?.FocusBox();
        if (field != null) OnFieldFocused(field);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Skip();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // MARK: - Floating chrome

    private void BuildFloatingChrome()
    {
        _stack.Location = new Point(1, 1); // 1px frame so the yellow hairline border shows
        Controls.Add(_stack);
        FormDrag.Enable(this, _stack);
    }

    // MARK: - Fullscreen chrome

    private void BuildFullscreenChrome()
    {
        Bounds = PromptGeometry.PromptScreen().Bounds;

        _cardPanel = new CardPanel
        {
            CardFill = Palette.White.OverBlack(0.04),
            CardBorder = Palette.Yellow.With(0.3),
            Radius = 20,
            AutoSize = false,
        };
        _stack.Location = Point.Empty;
        _cardPanel.Controls.Add(_stack);
        Controls.Add(_cardPanel);

        _logoRow = new Panel { BackColor = Palette.Black, Height = Brand.S(26) };
        var square = new Panel { BackColor = Palette.Yellow, Size = new Size(Brand.S(26), Brand.S(26)), Location = new Point(0, 0) };
        var word = new TrackedLabel
        {
            Text = "LLOYDS TRACKER",
            Font = Brand.Ui(11f, FontStyle.Bold),
            ForeColor = Palette.Gray,
            Tracking = 3f,
            BackColor = Palette.Black,
        };
        word.Location = new Point(square.Right + Brand.S(8), (Brand.S(26) - word.Height) / 2);
        _logoRow.Controls.Add(square);
        _logoRow.Controls.Add(word);
        _logoRow.Width = square.Width + Brand.S(8) + word.Width;
        Controls.Add(_logoRow);

        _fullscreenHint = new Label
        {
            AutoSize = true,
            Text = "Upiši što radiš i stisni Enter — ili preskoči (Esc), pa te period čeka u sljedećem promptu.",
            Font = Brand.Ui(9.5f),
            ForeColor = Palette.Gray.With(0.7),
            BackColor = Palette.Black,
        };
        Controls.Add(_fullscreenHint);
    }

    // MARK: - Layout

    private void Relayout()
    {
        _stack.PerformLayout();
        if (_big) LayoutFullscreen();
        else LayoutFloating();
    }

    private void LayoutFloating()
    {
        var size = _stack.PreferredSize;
        ClientSize = new Size(size.Width + 2, size.Height + 2);

        if (_floatRight == 0)
        {
            var wa = PromptGeometry.PromptScreen().WorkingArea;
            _floatRight = wa.Right - Brand.S(24);
            _floatTop = wa.Top + Brand.S(24);
        }
        Left = _floatRight - Width;
        Top = _floatTop;

        Region = new Region(Brand.RoundedRect(new RectangleF(0, 0, Width, Height), Brand.Sf(16)));
    }

    private void LayoutFullscreen()
    {
        if (_cardPanel == null || _logoRow == null || _fullscreenHint == null) return;

        _cardPanel.Size = _stack.PreferredSize;

        int spacing = Brand.S(28);
        int totalH = _logoRow.Height + spacing + _cardPanel.Height + spacing + _fullscreenHint.Height;
        int y = (ClientSize.Height - totalH) / 2;

        _logoRow.Location = new Point((ClientSize.Width - _logoRow.Width) / 2, y);
        y += _logoRow.Height + spacing;
        _cardPanel.Location = new Point((ClientSize.Width - _cardPanel.Width) / 2, y);
        y += _cardPanel.Height + spacing;
        _fullscreenHint.Location = new Point((ClientSize.Width - _fullscreenHint.Width) / 2, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_big)
        {
            // Yellow hairline border on the floating panel.
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Palette.Yellow.With(0.35), 1f);
            using var path = Brand.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Brand.Sf(16));
            e.Graphics.DrawPath(pen, path);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Relayout();
        // Fokus čeka isto koliko i prozor prije nego uzme tipkovnicu (PanelFade) — pop-up koji
        // iskoči dok tipkaš u drugoj aplikaciji ne smije presresti ostatak rečenice.
        // The prompt often opens while the session is locked or a monitor is asleep; the
        // screen layout can then change under it and the fullscreen form keeps the old
        // screen's size. Re-stick it to the current screen whenever that happens.
        SystemEvents.DisplaySettingsChanged += OnScreensChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _screenHooked = true;
    }

    private bool _screenHooked;

    private void OnScreensChanged(object? sender, EventArgs e) => RefitToScreen();

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect)
            RefitToScreen();
    }

    /// <summary>Fullscreen prompt covers exactly one screen; the floating panel is pulled back
    /// inside the working area if it ended up off-screen. Mirrors PromptController.refitToScreen.</summary>
    private void RefitToScreen()
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(new Action(RefitToScreen)); return; }

        var screen = Screen.FromControl(this);
        if (_big)
        {
            if (Bounds != screen.Bounds)
            {
                Bounds = screen.Bounds;
                LayoutFullscreen();
            }
        }
        else
        {
            var wa = screen.WorkingArea;
            int left = Math.Min(Math.Max(Left, wa.Left), Math.Max(wa.Left, wa.Right - Width));
            int top = Math.Min(Math.Max(Top, wa.Top), Math.Max(wa.Top, wa.Bottom - Height));
            if (left != Left || top != Top)
            {
                _floatRight = left + Width;
                _floatTop = top;
                Location = new Point(left, top);
            }
        }
    }

    // MARK: - Produženje perioda

    /// <summary>Produži period do nove granice (skupno vrijeme) bez zatvaranja prozora —
    /// upisani tekst i podjele ostaju. Zrcali macOS PromptController.extend.</summary>
    public void Extend(DateTime end)
    {
        var newEnd = Max(end, _periodStart);
        if (newEnd <= _periodEnd) return;

        SyncTextsFromFields();
        // Zadrži fokus na istom polju nakon rebuilda — ali samo ako je prompt trenutno
        // fokusiran (da produženje ne otima fokus dok si u drugoj aplikaciji).
        DateTime? refocusKey = ContainsFocus
            ? (_fields.FirstOrDefault(f => f.ContainsFocus)?.Key ?? _periodStart)
            : null;
        _periodEnd = newEnd;
        _boundaries = PromptGeometry.GridBoundaries(_periodStart, _periodEnd);

        if (_timeLabel != null)
        {
            _timeLabel.Text = $"{Fmt.Hhmm(_periodStart)} – {Fmt.Hhmm(_periodEnd)}";
            var sz = TextRenderer.MeasureText(_timeLabel.Text, _timeLabel.Font);
            _timeLabel.Width = sz.Width + Brand.S(4);
            _timeLabel.Location = new Point(_innerW - _timeLabel.Width, 0);
        }

        if (_blockBar != null)
        {
            _blockBar.PeriodEnd = _periodEnd;
            _blockBar.Boundaries = _boundaries;
            _blockBar.Invalidate();
        }
        else if (_boundaries.Count > 0)
        {
            // Period je prešao prag za mrežu blokova — dodaj traku prije segmenata.
            _blockBar = NewBlockBar();
            _mainPanel.Controls.Add(_blockBar);
            _mainPanel.Controls.SetChildIndex(_blockBar, _mainPanel.Controls.IndexOf(_segmentsPanel));
        }

        RebuildSegmentsUI();
        if (refocusKey is DateTime rk) FocusFieldAt(rk);
    }

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;

    protected override void Dispose(bool disposing)
    {
        if (disposing && _screenHooked)
        {
            SystemEvents.DisplaySettingsChanged -= OnScreensChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _screenHooked = false;
        }
        base.Dispose(disposing);
    }
}
