using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

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
        Action<IReadOnlyList<PromptSegment>> onSubmit,
        Action onSnooze)
    {
        if (IsVisible) return;

        var form = new PromptForm(
            request, style, history,
            onSubmit: segments => { Close(); onSubmit(segments); },
            onSnooze: () => { Close(); onSnooze(); });
        _form = form;
        form.FormClosed += (_, _) => { if (_form == form) _form = null; };
        form.Show();
        form.Activate();
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

    private bool _focused;

    public PromptTextField(DateTime key, bool big, Color fieldFill)
    {
        Key = key;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.Black;

        int pad = big ? 14 : 10;
        _fieldFill = fieldFill;
        _pad = pad;
        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Multiline = false,
            Font = Brand.Ui(big ? 15f : 12f),
            ForeColor = Palette.White,
            BackColor = fieldFill,
        };
        _box.GotFocus += (_, _) => { _focused = true; Invalidate(); FocusChanged?.Invoke(this); };
        _box.LostFocus += (_, _) => { _focused = false; Invalidate(); };
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
        _box.SetBounds(_pad, (Height - _box.Font.Height) / 2, Math.Max(10, Width - _pad * 2), _box.Font.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Brand.RoundedRect(r, 10);
        using (var b = new SolidBrush(_fieldFill)) g.FillPath(b, path);
        using var pen = new Pen(Palette.Yellow.With(_focused ? 0.8 : 0.25), 1f);
        g.DrawPath(pen, path);
    }
}

internal sealed class PromptForm : Form
{
    private readonly PromptRequest _request;
    private readonly PromptStyle _style;
    private readonly IReadOnlyList<string> _history;
    private readonly Action<IReadOnlyList<PromptSegment>> _onSubmit;
    private readonly Action _onSnooze;

    private readonly DateTime _periodStart;
    private readonly DateTime _periodEnd;
    private readonly List<DateTime> _boundaries;
    private readonly HashSet<DateTime> _splitPoints = new();
    private readonly Dictionary<DateTime, string> _texts = new();
    private readonly Dictionary<DateTime, string> _drafts = new();
    private readonly Dictionary<DateTime, int> _historyIndices = new();

    private readonly bool _big;
    private readonly int _pad;
    private readonly int _innerW;
    private readonly Color _interior;
    private readonly Color _fieldFill;

    private FlowLayoutPanel _stack = null!;
    private FlowLayoutPanel _segmentsPanel = null!;
    private Panel _hintsPanel = null!;
    private BlockBarControl? _blockBar;
    private CardPanel? _cardPanel;    // fullscreen only
    private Panel? _logoRow;          // fullscreen only
    private Label? _fullscreenHint;   // fullscreen only
    private readonly List<PromptTextField> _fields = new();

    private int _floatRight;
    private int _floatTop;

    private string Prefill => _history.Count > 0 ? _history[0] : "";

    public PromptForm(
        PromptRequest request,
        PromptStyle style,
        IReadOnlyList<string> history,
        Action<IReadOnlyList<PromptSegment>> onSubmit,
        Action onSnooze)
    {
        _request = request;
        _style = style;
        _history = history;
        _onSubmit = onSubmit;
        _onSnooze = onSnooze;

        _periodStart = request.Start;
        _periodEnd = Max(request.End ?? DateTime.Now, _periodStart);
        _boundaries = PromptGeometry.GridBoundaries(_periodStart, _periodEnd);
        _texts[_periodStart] = Prefill;

        _big = style == PromptStyle.Fullscreen;
        _pad = _big ? 28 : 18;
        int cardWidth = _big ? 560 : 420;
        _innerW = cardWidth - _pad * 2;
        _interior = _big ? Palette.White.OverBlack(0.04) : Palette.Black;
        _fieldFill = Palette.White.OverBlack(0.07);

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

        if (_boundaries.Count > 0)
        {
            _blockBar = new BlockBarControl
            {
                Width = _innerW,
                PeriodStart = _periodStart,
                PeriodEnd = _periodEnd,
                Boundaries = _boundaries,
                SplitPoints = _splitPoints,
                BackColor = _interior,
            };
            _blockBar.SplitToggled += ToggleSplit;
            _blockBar.SegmentFocused += FocusFieldAt;
            AddRow(_blockBar);
        }

        _segmentsPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = _interior,
            Margin = new Padding(0, 14, 0, 0),
            Width = _innerW,
        };
        AddRow(_segmentsPanel);

        _hintsPanel = new Panel { Width = _innerW, BackColor = _interior, Margin = new Padding(0, 14, 0, 0) };
        AddRow(_hintsPanel);

        RebuildSegmentsUI();
    }

    private void AddRow(Control c, bool first = false)
    {
        c.Margin = new Padding(0, first ? 0 : 14, 0, 0);
        if (c.Width == 0) c.Width = _innerW;
        _stack.Controls.Add(c);
    }

    private Panel BuildHeaderRow()
    {
        var titleFont = Brand.Ui(_big ? 20f : 13f, FontStyle.Bold);
        int h = Math.Max(titleFont.Height, 20);
        var row = new Panel { Width = _innerW, Height = h, BackColor = _interior };

        var dot = new Dot { Color = Palette.Yellow, BackColor = _interior, Size = new Size(9, 9) };
        dot.Location = new Point(0, (h - 9) / 2);
        row.Controls.Add(dot);

        var title = new TrackedLabel
        {
            Text = "NA ČEMU RADIŠ?",
            Font = titleFont,
            ForeColor = Palette.White,
            Tracking = 1.5f,
            BackColor = _interior,
        };
        title.Location = new Point(dot.Right + 8, (h - title.Height) / 2);
        row.Controls.Add(title);

        var timeFont = Brand.Mono(_big ? 10.5f : 9f, FontStyle.Regular);
        var timeText = $"{Fmt.Hhmm(_periodStart)} – {Fmt.Hhmm(_periodEnd)}";
        var timeSize = TextRenderer.MeasureText(timeText, timeFont);
        var timeLabel = new Label
        {
            AutoSize = false,
            Text = timeText,
            Font = timeFont,
            ForeColor = Palette.Gray,
            BackColor = _interior,
            TextAlign = ContentAlignment.MiddleRight,
            Width = timeSize.Width + 4,
            Height = h,
        };
        timeLabel.Location = new Point(_innerW - timeLabel.Width, 0);
        timeLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        row.Controls.Add(timeLabel);

        return row;
    }

    // MARK: - Segment rows

    private List<(DateTime Start, DateTime End)> Segments()
        => PromptGeometry.Segments(_periodStart, _periodEnd, _boundaries, _splitPoints);

    private static Label WrappedLabel(string text, Font font, Color fore, Color back, int width)
    {
        var l = new Label { AutoSize = false, Text = text, Font = font, ForeColor = fore, BackColor = back, Width = width };
        l.Height = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 2;
        return l;
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
            var field = new PromptTextField(seg.Start, _big, _fieldFill);
            field.Value = _texts.GetValueOrDefault(seg.Start, "");
            field.SubmitRequested += Submit;
            field.EscapeRequested += HandleEscape;
            field.HistoryOlder += () => CycleHistory(field, older: true);
            field.HistoryNewer += () => CycleHistory(field, older: false);
            field.FocusChanged += OnFieldFocused;
            _fields.Add(field);

            if (single)
            {
                field.Width = _innerW;
                field.Margin = new Padding(0, _segmentsPanel.Controls.Count == 0 ? 0 : 8, 0, 0);
                _segmentsPanel.Controls.Add(field);
            }
            else
            {
                int labelW = _big ? 96 : 80;
                var rowPanel = new Panel
                {
                    Width = _innerW,
                    Height = field.Height,
                    BackColor = _interior,
                    Margin = new Padding(0, _segmentsPanel.Controls.Count == 0 ? 0 : 8, 0, 0),
                };
                var label = new Label
                {
                    AutoSize = false,
                    Text = $"{Fmt.Hhmm(seg.Start)}–{Fmt.Hhmm(seg.End)}",
                    Font = Brand.Mono(_big ? 9.5f : 8f, FontStyle.Bold),
                    ForeColor = Palette.Gray,
                    BackColor = _interior,
                    Width = labelW,
                    Height = field.Height,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Location = new Point(0, 0),
                };
                field.Width = _innerW - labelW - 8;
                field.Location = new Point(labelW + 8, 0);
                rowPanel.Controls.Add(label);
                rowPanel.Controls.Add(field);
                _segmentsPanel.Controls.Add(rowPanel);
            }
        }
        _segmentsPanel.ResumeLayout(true);

        if (_blockBar != null)
        {
            _blockBar.SingleSegment = single;
            _blockBar.Invalidate();
        }

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
            x += chip.Width + 4;

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
            x += lbl.Width + 14;
        }

        AddHint("↑↓", "povijest");
        AddHint("Enter", "spremi");
        if (single)
        {
            if (_style == PromptStyle.Floating && !string.IsNullOrEmpty(Prefill))
                AddHint("Esc", "isto kao zadnje");
            if (_boundaries.Count > 0)
                AddHint("✂", "razbij period");
        }

        _hintsPanel.Height = Math.Max(chipH, 16);

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
                Height = _hintsPanel.Height,
                Width = 100,
            };
            snooze.Location = new Point(_innerW - snooze.Width, 0);
            snooze.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            snooze.Click += (_, _) => _onSnooze();
            _hintsPanel.Controls.Add(snooze);
        }

        _hintsPanel.ResumeLayout(true);
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

    private void Submit()
    {
        SyncTextsFromFields();
        var outSegs = new List<PromptSegment>();
        foreach (var seg in Segments())
        {
            string t = _texts.GetValueOrDefault(seg.Start, "").Trim();
            if (t.Length == 0)
            {
                FocusFieldAt(seg.Start);
                return;
            }
            outSegs.Add(new PromptSegment(seg.Start, seg.End, t));
        }
        if (outSegs.Count == 0) return;
        _onSubmit(outSegs);
    }

    private void HandleEscape()
    {
        if (_style == PromptStyle.Floating && Segments().Count == 1 && !string.IsNullOrEmpty(Prefill))
            _onSubmit(new List<PromptSegment> { new(_periodStart, _periodEnd, Prefill) });
        // fullscreen or split: esc does nothing
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
    }

    private void FocusFieldAt(DateTime start)
    {
        var field = _fields.FirstOrDefault(f => f.Key == start) ?? _fields.FirstOrDefault();
        field?.FocusBox();
        if (field != null) OnFieldFocused(field);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            HandleEscape();
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
        var screen = Screen.FromPoint(Cursor.Position) ?? Screen.PrimaryScreen!;
        Bounds = screen.Bounds;

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

        _logoRow = new Panel { BackColor = Palette.Black, Height = 26 };
        var square = new Panel { BackColor = Palette.Yellow, Size = new Size(26, 26), Location = new Point(0, 0) };
        var word = new TrackedLabel
        {
            Text = "LLOYDS TRACKER",
            Font = Brand.Ui(11f, FontStyle.Bold),
            ForeColor = Palette.Gray,
            Tracking = 3f,
            BackColor = Palette.Black,
        };
        word.Location = new Point(square.Right + 8, (26 - word.Height) / 2);
        _logoRow.Controls.Add(square);
        _logoRow.Controls.Add(word);
        _logoRow.Width = square.Width + 8 + word.Width;
        Controls.Add(_logoRow);

        _fullscreenHint = new Label
        {
            AutoSize = true,
            Text = "Odgovor je obavezan — upiši što radiš i stisni Enter.",
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
            var wa = Screen.PrimaryScreen!.WorkingArea;
            _floatRight = wa.Right - 24;
            _floatTop = wa.Top + 24;
        }
        Left = _floatRight - Width;
        Top = _floatTop;

        Region = new Region(Brand.RoundedRect(new RectangleF(0, 0, Width, Height), 16));
    }

    private void LayoutFullscreen()
    {
        if (_cardPanel == null || _logoRow == null || _fullscreenHint == null) return;

        _cardPanel.Size = _stack.PreferredSize;

        int spacing = 28;
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
            using var path = Brand.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 16);
            e.Graphics.DrawPath(pen, path);
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Relayout();
        BeginInvoke(new Action(() => FocusFieldAt(_periodStart)));
    }

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;
}
