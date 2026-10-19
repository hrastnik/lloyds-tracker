using System.Drawing;
using System.Windows.Forms;

namespace LloydsTracker;

/// <summary>"Pregled dana" — grouped/chronological day view with copy, CSV export,
/// inline editing/adding of entries and day navigation. Mirrors the SwiftUI SummaryView.</summary>
internal sealed class SummaryForm : Form
{
    private enum ViewMode { Grouped, Chronological }

    private readonly TrackerEngine _engine;
    /// <summary>Tooltip za gumbe u redovima (macOS `.help(…)`).</summary>
    private readonly ToolTip _tips = new();
    private DateTime _date = DateTime.Now;
    private ViewMode _mode = ViewMode.Grouped;

    private Panel _header = null!;
    private Panel _content = null!;
    private Panel _footer = null!;
    private Label _dayLabel = null!;
    private Label _workBadge = null!;
    private Label _pauseBadge = null!;
    private FlatButton _prevButton = null!;
    private FlatButton _nextButton = null!;
    /// <summary>Uređivač unosa u kronološkom pregledu (null = zatvoren). Preživljava
    /// <see cref="LayoutContent"/>, pa promjena u engineu ne prekida tipkanje.</summary>
    private EntryEditor? _editor;
    private FlatButton _copyButton = null!;
    private FlatButton _groupedTab = null!;
    private FlatButton _chronoTab = null!;
    private FlatButton _mergeToggle = null!;

    private string DayKey => Store.DayKey(_date);
    private IReadOnlyList<Entry> Entries => DayKey == _engine.CurrentDayKey ? _engine.Entries : Store.LoadDay(DayKey);

    public SummaryForm(TrackerEngine engine)
    {
        _engine = engine;
        Text = "Pregled dana";
        // Brand tile in the title bar / taskbar / Alt+Tab, like the macOS AppIcon.
        Icon = TrayIconFactory.Window;
        MinimumSize = new Size(Brand.S(520), Brand.S(440));
        ClientSize = new Size(Brand.S(560), Brand.S(520));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Palette.Black;
        Font = Brand.Ui(9f);

        BuildChrome();
        _engine.Changed += OnEngineChanged;
        Rebuild();
    }

    private void OnEngineChanged()
    {
        if (DayKey == _engine.CurrentDayKey) Rebuild();
    }

    public void RefreshData() => Rebuild();

    private void BuildChrome()
    {
        _footer = new Panel { Dock = DockStyle.Bottom, Height = Brand.S(52), BackColor = Palette.Black };
        _footer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = Brand.S(1), BackColor = Palette.White.OverBlack(0.15) });
        _header = new Panel { Dock = DockStyle.Top, Height = Brand.S(108), BackColor = Palette.Black };
        _header.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = Brand.S(1), BackColor = Palette.White.OverBlack(0.15) });
        _content = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Black, AutoScroll = true };

        // Fill added first (lowest z-order → laid out last, takes remaining space);
        // edge-docked header/footer added after.
        Controls.Add(_content);
        Controls.Add(_header);
        Controls.Add(_footer);

        BuildHeader();
        BuildFooter();
        _content.SizeChanged += (_, _) => LayoutContent();
    }

    private void BuildHeader()
    {
        // Strelice dana: velika pločica s hover podlogom — cijela prima klik, ne samo glif.
        _prevButton = DayButton("◀", "Prethodni dan");
        _prevButton.Location = new Point(Brand.S(12), Brand.S(10));
        _prevButton.Click += (_, _) => ShiftDay(-1);
        _header.Controls.Add(_prevButton);

        _nextButton = DayButton("▶", "Sljedeći dan");
        _nextButton.Click += (_, _) => { if (!IsToday()) ShiftDay(1); };
        _nextButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _nextButton.Location = new Point(ClientSize.Width - Brand.S(12) - _nextButton.Width, Brand.S(10));
        _header.Controls.Add(_nextButton);

        _dayLabel = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Font = Brand.Ui(11.5f, FontStyle.Bold), ForeColor = Palette.White, BackColor = Palette.Black, Location = new Point(Brand.S(60), Brand.S(10)), Size = new Size(ClientSize.Width - Brand.S(120), Brand.S(34)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _header.Controls.Add(_dayLabel);

        _workBadge = MakeBadge("RAD", Palette.Yellow, new Point(Brand.S(16), Brand.S(56)));
        _pauseBadge = MakeBadge("PAUZE", Palette.Gray, new Point(0, Brand.S(56)));
        _header.Controls.Add(_workBadge);
        _header.Controls.Add(_pauseBadge);

        _groupedTab = MakeTab("Grupirano", ViewMode.Grouped);
        _chronoTab = MakeTab("Kronološki", ViewMode.Chronological);
        _groupedTab.Anchor = _chronoTab.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _header.Controls.Add(_groupedTab);
        _header.Controls.Add(_chronoTab);

        // Merging is a persisted setting, but it's toggled here since it only affects the
        // chronological view.
        _mergeToggle = new FlatButton
        {
            Font = Brand.Ui(9f, FontStyle.Bold),
            CornerRadius = 6,
            BackColor = Palette.Black,
            Align = ContentAlignment.MiddleLeft,
            Size = new Size(Brand.S(260), Brand.S(24)),
            Location = new Point(Brand.S(16), Brand.S(90)),
        };
        _mergeToggle.Click += (_, _) =>
        {
            CloseEditor(relayout: false);
            _engine.MutateSettings(s => s.MergeAdjacentEntries = !s.MergeAdjacentEntries);
            Rebuild();
        };
        _header.Controls.Add(_mergeToggle);
    }

    private FlatButton DayButton(string glyph, string tip)
    {
        var button = new FlatButton
        {
            Text = glyph, TextColor = Palette.Yellow, Fill = Palette.Black, CornerRadius = 8,
            Font = Brand.Ui(11f, FontStyle.Bold), BackColor = Palette.Black, Size = new Size(Brand.S(44), Brand.S(34)),
        };
        _tips.SetToolTip(button, tip);
        return button;
    }

    private void ShiftDay(int days)
    {
        CloseEditor(relayout: false);
        _date = _date.AddDays(days);
        Rebuild();
    }

    private Label MakeBadge(string label, Color color, Point loc)
        => new()
        {
            AutoSize = false,
            Text = "",
            BackColor = Palette.White.OverBlack(0.06),
            Location = loc,
            Size = new Size(Brand.S(120), Brand.S(26)),
            Padding = new Padding(Brand.S(8), 0, Brand.S(8), 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Tag = (label, color),
        };

    private void RenderBadge(Label badge, string value)
    {
        var (label, color) = ((string, Color))badge.Tag!;
        badge.Text = $"{label}  {value}";
        badge.Font = Brand.Mono(9.5f, FontStyle.Bold);
        badge.ForeColor = color;
    }

    private FlatButton MakeTab(string text, ViewMode mode)
    {
        var tab = new FlatButton
        {
            Text = text,
            Font = Brand.Ui(9f, FontStyle.Bold),
            CornerRadius = 6,
            BackColor = Palette.Black,
            Size = new Size(Brand.S(96), Brand.S(26)),
        };
        tab.Click += (_, _) =>
        {
            if (mode == ViewMode.Grouped) CloseEditor(relayout: false);
            _mode = mode;
            Rebuild();
        };
        return tab;
    }

    private void PositionTabs()
    {
        int right = ClientSize.Width - Brand.S(16);
        _chronoTab.Location = new Point(right - _chronoTab.Width, Brand.S(56));
        _groupedTab.Location = new Point(_chronoTab.Left - _groupedTab.Width - Brand.S(2), Brand.S(56));
    }

    private void BuildFooter()
    {
        _copyButton = new FlatButton { Text = "Kopiraj pregled", TextColor = Palette.Yellow, Fill = Palette.Yellow.With(0.12), BorderColor = Palette.Yellow.With(0.5), BorderWidth = 1, CornerRadius = 6, Font = Brand.Ui(9f, FontStyle.Bold), BackColor = Palette.Black, Size = new Size(Brand.S(140), Brand.S(28)), Location = new Point(Brand.S(12), Brand.S(12)) };
        _copyButton.Click += (_, _) => CopyOverview();
        _footer.Controls.Add(_copyButton);

        var csv = new FlatButton { Text = "Export CSV…", TextColor = Palette.Yellow, Fill = Palette.Yellow.With(0.12), BorderColor = Palette.Yellow.With(0.5), BorderWidth = 1, CornerRadius = 6, Font = Brand.Ui(9f, FontStyle.Bold), BackColor = Palette.Black, Size = new Size(Brand.S(120), Brand.S(28)), Location = new Point(Brand.S(160), Brand.S(12)) };
        csv.Click += (_, _) => ExportCsv();
        _footer.Controls.Add(csv);

        var folder = new FlatButton { Text = "Otvori folder s podacima", TextColor = Palette.Gray, Font = Brand.Ui(9f), BackColor = Palette.Black, Size = new Size(Brand.S(180), Brand.S(28)), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        folder.Location = new Point(ClientSize.Width - Brand.S(12) - folder.Width, Brand.S(12));
        folder.Click += (_, _) => OpenDataFolder();
        _footer.Controls.Add(folder);
    }

    private bool IsToday() => DayKey == Store.DayKey(DateTime.Now);

    private void Rebuild()
    {
        _dayLabel.Text = Fmt.DayTitle(_date);
        _nextButton.Enabled = !IsToday();
        _nextButton.TextColor = IsToday() ? Palette.Yellow.OverBlack(0.3) : Palette.Yellow;
        _nextButton.Invalidate();
        RenderBadge(_workBadge, Fmt.Dur(Summarize.WorkTotal(Entries)));
        RenderBadge(_pauseBadge, Fmt.Dur(Summarize.PauseTotal(Entries)));
        _pauseBadge.Location = new Point(_workBadge.Right + Brand.S(8), Brand.S(56));

        bool grouped = _mode == ViewMode.Grouped;
        StyleTab(_groupedTab, grouped);
        StyleTab(_chronoTab, !grouped);
        PositionTabs();

        // The merge toggle only applies to the chronological view — the header grows by its row.
        bool merge = _engine.Settings.MergeAdjacentEntries;
        _mergeToggle.Visible = !grouped;
        _mergeToggle.Text = (merge ? "☑" : "☐") + "  Spoji susjedne unose istog naziva";
        _mergeToggle.Width = TextRenderer.MeasureText(_mergeToggle.Text, _mergeToggle.Font).Width + Brand.S(24);
        _mergeToggle.TextColor = merge ? Palette.Yellow : Palette.Gray;
        _mergeToggle.Fill = Palette.White.OverBlack(merge ? 0.06 : 0.03);
        _mergeToggle.Invalidate();
        _header.Height = Brand.S(grouped ? 108 : 128);

        LayoutContent();
    }

    private void StyleTab(FlatButton tab, bool active)
    {
        tab.Fill = active ? Palette.Yellow : Color.Transparent;
        tab.TextColor = active ? Palette.Black : Palette.Gray;
        tab.BorderColor = active ? Color.Transparent : Palette.White.OverBlack(0.2);
        tab.BorderWidth = active ? 0 : 1;
        tab.Invalidate();
    }

    private void LayoutContent()
    {
        _content.SuspendLayout();
        // Otvoreni uređivač se ne dira — zadržava fokus i upisani tekst.
        foreach (Control c in _content.Controls.Cast<Control>().ToList())
        {
            if (c == _editor) continue;
            c.Dispose();
        }

        var entries = Entries;
        int width = _content.ClientSize.Width - Brand.S(32);
        if (width < Brand.S(40)) { _content.ResumeLayout(true); return; }

        // Pozicije su relativne na vidljivi dio, pa se pomak skrolanja uračunava.
        int top = Brand.S(16) + _content.AutoScrollPosition.Y;

        if (entries.Count == 0 && _editor == null)
        {
            var empty = new Label { AutoSize = true, Text = "Nema unosa za ovaj dan.", ForeColor = Palette.Gray, BackColor = Palette.Black, Font = Brand.Ui(10f), Location = new Point(Brand.S(16), top + Brand.S(8)) };
            _content.Controls.Add(empty);
            var add = AddButton(Brand.S(200));
            add.Location = new Point(Brand.S(16), empty.Bottom + Brand.S(12));
            _content.Controls.Add(add);
            _content.ResumeLayout(true);
            return;
        }

        int y = top;
        if (_mode == ViewMode.Grouped)
        {
            foreach (var group in Summarize.Groups(entries))
                y = AddGroupRow(group, y, width);
        }
        else
        {
            bool placed = false;
            var rows = Summarize.Chronology(entries, _engine.Settings.MergeAdjacentEntries);
            foreach (var item in Summarize.Timeline(rows))
            {
                if (item.Row is ChronoRow row)
                {
                    if (_editor?.Draft.Row is ChronoRow editing && editing.Ids[0] == row.Ids[0])
                    {
                        y = PlaceEditor(y, width);
                        placed = true;
                    }
                    else
                    {
                        y = AddChronoRow(row, y, width);
                    }
                }
                else if (_editor != null && _editor.Draft.Row == null && _editor.Draft.GapStart == item.GapStart)
                {
                    y = PlaceEditor(y, width);
                    placed = true;
                }
                else
                {
                    y = AddGapRow(item.GapStart, item.GapEnd, y, width);
                }
            }

            if (_editor != null && _editor.Draft.Row == null && _editor.Draft.GapStart == null)
            {
                y = PlaceEditor(y + Brand.S(4), width);
                placed = true;
            }
            else
            {
                var add = AddButton(width);
                add.Location = new Point(Brand.S(16), y + Brand.S(4));
                _content.Controls.Add(add);
            }

            // Red koji se uređivao je nestao (npr. obrisan iz drugog prozora).
            if (_editor != null && !placed) CloseEditor(relayout: false);
        }
        _content.ResumeLayout(true);
    }

    private int PlaceEditor(int y, int width)
    {
        var editor = _editor!;
        editor.SetBounds(Brand.S(16), y, width, editor.Height);
        if (editor.Parent != _content) _content.Controls.Add(editor);
        return y + editor.Height + Brand.S(6);
    }

    private int AddGroupRow(GroupSummary group, int y, int width)
    {
        var row = new CardPanel { CardFill = Palette.White.OverBlack(0.04), BorderWidth = 0, Radius = 8, Location = new Point(Brand.S(16), y), Size = new Size(width, Brand.S(52)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

        var dur = new Label { AutoSize = false, Text = Fmt.Dur(group.Total), Font = Brand.Mono(9.5f, FontStyle.Bold), ForeColor = Palette.Yellow, BackColor = row.CardFill, Location = new Point(Brand.S(10), Brand.S(8)), Size = new Size(Brand.S(70), Brand.S(18)), TextAlign = ContentAlignment.MiddleLeft };
        row.Controls.Add(dur);

        var text = new Label { AutoSize = false, AutoEllipsis = true, Text = group.Text, Font = Brand.Ui(9.5f, FontStyle.Bold), ForeColor = Palette.White, BackColor = row.CardFill, Location = new Point(Brand.S(80), Brand.S(8)), Size = new Size(width - Brand.S(90), Brand.S(18)), TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        row.Controls.Add(text);

        string ranges = string.Join(" · ", group.Ranges.Select(r => $"{Fmt.Hhmm(r.Start)}–{Fmt.Hhmm(r.End)}"));
        var rangesLabel = new Label { AutoSize = false, Text = ranges, Font = Brand.Mono(8f), ForeColor = Palette.Gray.With(0.7), BackColor = row.CardFill, Location = new Point(Brand.S(80), Brand.S(28)), Size = new Size(width - Brand.S(90), Brand.S(16)), TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        row.Controls.Add(rangesLabel);

        _content.Controls.Add(row);
        return y + Brand.S(52) + Brand.S(8);
    }

    /// <summary>Cijeli red je gumb — klik otvara uređivač na mjestu reda.</summary>
    private int AddChronoRow(ChronoRow entry, int y, int width)
    {
        var row = new ChronoRowControl(entry) { Location = new Point(Brand.S(16), y), Width = width, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _tips.SetToolTip(row, entry.IsMerged ? $"Uredi unos ({entry.Ids.Count} bloka)" : "Uredi unos");
        row.Click += (_, _) => OpenEditor(EntryDraft.Edit(entry));
        _content.Controls.Add(row);
        return y + row.Height + Brand.S(6);
    }

    /// <summary>Nezabilježeno vrijeme između dva unosa — klik ga popuni novim unosom.</summary>
    private int AddGapRow(DateTime start, DateTime end, int y, int width)
    {
        var gap = new FlatButton
        {
            Text = $"+   {Fmt.Hhmm(start)}–{Fmt.Hhmm(end)}   nezabilježeno · {Fmt.Dur((end - start).TotalSeconds)}",
            TextColor = Palette.Gray.OverBlack(0.55), BorderColor = Palette.White.OverBlack(0.14), BorderWidth = 1,
            CornerRadius = 6, Font = Brand.Ui(8.5f), Align = ContentAlignment.MiddleLeft, BackColor = Palette.Black,
            Location = new Point(Brand.S(16), y), Size = new Size(width, Brand.S(26)),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _tips.SetToolTip(gap, $"Dodaj unos za {Fmt.Hhmm(start)}–{Fmt.Hhmm(end)}");
        gap.Click += (_, _) => OpenEditor(EntryDraft.New(start, end, start));
        _content.Controls.Add(gap);
        return y + gap.Height + Brand.S(6);
    }

    /// <summary>Novi unos na kraju dana (od kraja zadnjeg unosa) — i iz praznog dana.</summary>
    private FlatButton AddButton(int width)
    {
        var add = new FlatButton
        {
            Text = "+  Dodaj unos", TextColor = Palette.Yellow, Fill = Palette.Yellow.With(0.06),
            BorderColor = Palette.Yellow.With(0.4), BorderWidth = 1, CornerRadius = 6,
            Font = Brand.Ui(9f, FontStyle.Bold), BackColor = Palette.Black, Size = new Size(width, Brand.S(30)),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        add.Click += (_, _) =>
        {
            var (start, end) = Summarize.SuggestedRange(Entries, _date, _engine.Settings);
            _mode = ViewMode.Chronological;
            OpenEditor(EntryDraft.New(start, end, null));
        };
        return add;
    }

    // MARK: - Uređivač

    private void OpenEditor(EntryDraft draft)
    {
        CloseEditor(relayout: false);
        string dayKey = DayKey;
        var editor = new EntryEditor(draft, _date, () => Entries, _engine.History);
        editor.SaveRequested += (text, start, end, kind) =>
        {
            if (draft.Row is ChronoRow row) _engine.UpdateEntries(row, dayKey, text, start, end, kind);
            else _engine.AddEntry(dayKey, text, start, end, kind);
            CloseEditor();
        };
        editor.DeleteRequested += () =>
        {
            if (draft.Row is ChronoRow row) _engine.DeleteEntries(row.Ids, dayKey);
            CloseEditor();
        };
        editor.CancelRequested += () => CloseEditor();
        _editor = editor;
        Rebuild();
        editor.FocusText();
    }

    /// <summary>Zatvara uređivač; <paramref name="relayout"/>=false kad pozivatelj ionako
    /// odmah radi <see cref="Rebuild"/>.</summary>
    private void CloseEditor(bool relayout = true)
    {
        if (_editor == null) return;
        var editor = _editor;
        _editor = null;
        _content.Controls.Remove(editor);
        // Dispose tek nakon što event handler (Spremi/Odustani) završi.
        BeginInvoke(new Action(editor.Dispose));
        if (relayout) Rebuild();
    }

    // MARK: - Footer actions

    private bool _copied;

    private void CopyOverview()
    {
        string text = Summarize.ClipboardText(_date, Entries);
        try { Clipboard.SetText(text); } catch { /* clipboard can be transiently locked */ }
        if (_copied) return;
        _copied = true;
        _copyButton.Text = "Kopirano ✓";
        var t = new System.Windows.Forms.Timer { Interval = 2000 };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); _copied = false; if (!_copyButton.IsDisposed) _copyButton.Text = "Kopiraj pregled"; };
        t.Start();
    }

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog
        {
            FileName = $"lloyds-tracker-{DayKey}.csv",
            Filter = "CSV (*.csv)|*.csv",
            DefaultExt = "csv",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            try { File.WriteAllText(dialog.FileName, Summarize.Csv(Entries), new System.Text.UTF8Encoding(false)); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Greška", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

    private void OpenDataFolder()
    {
        try { System.Diagnostics.Process.Start("explorer.exe", Store.Directory); }
        catch { /* best effort */ }
    }

    /// <summary>Red kronološkog pregleda kao jedan gumb — klik bilo gdje otvara uređivač,
    /// a olovka desno je samo vizualni znak da se red uređuje.</summary>
    private sealed class ChronoRowControl : Control
    {
        private readonly ChronoRow _row;
        private bool _hover;

        public ChronoRowControl(ChronoRow row)
        {
            _row = row;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Black;
            Cursor = Cursors.Hand;
            Height = Brand.S(32);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            using (var path = Brand.RoundedRect(new RectangleF(0, 0, Width, Height), Brand.Sf(6)))
            using (var b = new SolidBrush(Palette.White.OverBlack(_hover ? 0.08 : 0.03)))
                g.FillPath(b, path);

            const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            bool pause = _row.Kind == EntryKind.Pause;
            using var mono = Brand.Mono(8.5f);
            using var monoBold = Brand.Mono(8f, FontStyle.Bold);
            using var ui = Brand.Ui(9f, pause ? FontStyle.Italic : FontStyle.Regular);
            using var glyph = Brand.Ui(9.5f);

            int x = Brand.S(10);
            string time = $"{Fmt.Hhmm(_row.Start)}–{Fmt.Hhmm(_row.End)}";
            var timeSize = TextRenderer.MeasureText(g, time, mono, Size.Empty, flags);
            TextRenderer.DrawText(g, time, mono, new Rectangle(x, 0, timeSize.Width, Height), Palette.Gray, flags);
            x += timeSize.Width + Brand.S(10);

            int right = Width - Brand.S(10);
            var pencilColor = _hover ? Palette.Yellow : Palette.Gray.OverBlack(0.35);
            var pencilSize = TextRenderer.MeasureText(g, "✎", glyph, Size.Empty, flags);
            TextRenderer.DrawText(g, "✎", glyph, new Rectangle(right - pencilSize.Width, 0, pencilSize.Width, Height), pencilColor, flags);
            right -= pencilSize.Width + Brand.S(10);

            string dur = Fmt.Dur(_row.Duration);
            var durSize = TextRenderer.MeasureText(g, dur, mono, Size.Empty, flags);
            TextRenderer.DrawText(g, dur, mono, new Rectangle(right - durSize.Width, 0, durSize.Width, Height), Palette.Gray.OverBlack(0.7), flags);
            right -= durSize.Width + Brand.S(8);

            if (_row.IsMerged)
            {
                string count = $"{_row.Ids.Count}×";
                var countSize = TextRenderer.MeasureText(g, count, monoBold, Size.Empty, flags);
                TextRenderer.DrawText(g, count, monoBold, new Rectangle(right - countSize.Width, 0, countSize.Width, Height), Palette.Yellow.OverBlack(0.6), flags);
                right -= countSize.Width + Brand.S(8);
            }

            TextRenderer.DrawText(g, _row.Text, ui, new Rectangle(x, 0, Math.Max(0, right - x), Height),
                pause ? Palette.Gray.OverBlack(0.6) : Palette.White, flags | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_groupedTab != null) PositionTabs();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _engine.Changed -= OnEngineChanged;
        base.Dispose(disposing);
    }
}
