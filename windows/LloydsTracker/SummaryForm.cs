using System.Drawing;
using System.Windows.Forms;

namespace LloydsTracker;

/// <summary>"Pregled dana" — grouped/chronological day view with copy, CSV export,
/// per-entry delete and day navigation. Mirrors the SwiftUI SummaryView.</summary>
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
    private FlatButton _nextButton = null!;
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
        var prev = new FlatButton { Text = "◀", TextColor = Palette.Yellow, Font = Brand.Ui(11f, FontStyle.Bold), BackColor = Palette.Black, Size = new Size(Brand.S(30), Brand.S(26)), Location = new Point(Brand.S(16), Brand.S(14)) };
        prev.Click += (_, _) => { _date = _date.AddDays(-1); Rebuild(); };
        _header.Controls.Add(prev);

        _nextButton = new FlatButton { Text = "▶", TextColor = Palette.Yellow, Font = Brand.Ui(11f, FontStyle.Bold), BackColor = Palette.Black, Size = new Size(Brand.S(30), Brand.S(26)) };
        _nextButton.Click += (_, _) => { if (!IsToday()) { _date = _date.AddDays(1); Rebuild(); } };
        _nextButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _nextButton.Location = new Point(ClientSize.Width - Brand.S(16) - Brand.S(30), Brand.S(14));
        _header.Controls.Add(_nextButton);

        _dayLabel = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Font = Brand.Ui(11.5f, FontStyle.Bold), ForeColor = Palette.White, BackColor = Palette.Black, Location = new Point(Brand.S(52), Brand.S(14)), Size = new Size(ClientSize.Width - Brand.S(104), Brand.S(26)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
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
            _engine.MutateSettings(s => s.MergeAdjacentEntries = !s.MergeAdjacentEntries);
            Rebuild();
        };
        _header.Controls.Add(_mergeToggle);
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
        tab.Click += (_, _) => { _mode = mode; Rebuild(); };
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
        foreach (Control c in _content.Controls.Cast<Control>().ToList()) c.Dispose();
        _content.Controls.Clear();

        var entries = Entries;
        int width = _content.ClientSize.Width - Brand.S(32);
        if (width < Brand.S(40)) { _content.ResumeLayout(true); return; }

        if (entries.Count == 0)
        {
            var empty = new Label { AutoSize = true, Text = "Nema unosa za ovaj dan.", ForeColor = Palette.Gray, BackColor = Palette.Black, Font = Brand.Ui(10f), Location = new Point(Brand.S(16), Brand.S(24)) };
            _content.Controls.Add(empty);
            _content.ResumeLayout(true);
            return;
        }

        int y = Brand.S(16);
        if (_mode == ViewMode.Grouped)
        {
            foreach (var group in Summarize.Groups(entries))
                y = AddGroupRow(group, y, width);
        }
        else
        {
            foreach (var row in Summarize.Chronology(entries, _engine.Settings.MergeAdjacentEntries))
                y = AddChronoRow(row, y, width);
        }
        _content.ResumeLayout(true);
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

    private int AddChronoRow(ChronoRow entry, int y, int width)
    {
        bool pause = entry.Kind == EntryKind.Pause;
        var fill = Palette.White.OverBlack(0.03);
        var row = new CardPanel { CardFill = fill, BorderWidth = 0, Radius = 6, Location = new Point(Brand.S(16), y), Size = new Size(width, Brand.S(30)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

        var time = new Label { AutoSize = true, Text = $"{Fmt.Hhmm(entry.Start)}–{Fmt.Hhmm(entry.End)}", Font = Brand.Mono(8.5f), ForeColor = Palette.Gray, BackColor = fill, Location = new Point(Brand.S(10), Brand.S(7)) };
        row.Controls.Add(time);

        var del = new FlatButton { Text = "✕", TextColor = Palette.Gray.With(0.6), Font = Brand.Ui(8f, FontStyle.Bold), BackColor = fill, Size = new Size(Brand.S(22), Brand.S(22)), Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(width - Brand.S(8) - Brand.S(22), Brand.S(4)) };
        del.Click += (_, _) => { _engine.DeleteEntries(entry.Ids, DayKey); Rebuild(); };
        row.Controls.Add(del);

        // Ispravak unosa — spojeni red se ispravlja kao jedna cjelina (vidi EntryEditForm).
        var edit = new FlatButton { Text = "✎", TextColor = Palette.Gray.With(0.5), Font = Brand.Ui(9f), BackColor = fill, Size = new Size(Brand.S(22), Brand.S(22)), Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(del.Left - Brand.S(2) - Brand.S(22), Brand.S(4)) };
        _tips.SetToolTip(edit, entry.IsMerged ? $"Ispravi unos ({entry.Ids.Count} bloka)" : "Ispravi unos");
        edit.Click += (_, _) => EditEntry(entry);
        row.Controls.Add(edit);

        var dur = new Label { AutoSize = true, Text = Fmt.Dur(entry.Duration), Font = Brand.Mono(8.5f), ForeColor = Palette.Gray.With(0.7), BackColor = fill, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        dur.Location = new Point(edit.Left - Brand.S(8) - dur.PreferredWidth, Brand.S(7));
        row.Controls.Add(dur);

        int textRight = dur.Left - Brand.S(8);
        if (entry.IsMerged)
        {
            var count = new Label { AutoSize = true, Text = $"{entry.Ids.Count}×", Font = Brand.Mono(8f, FontStyle.Bold), ForeColor = Palette.Yellow.With(0.6), BackColor = fill, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            count.Location = new Point(dur.Left - Brand.S(8) - count.PreferredWidth, Brand.S(7));
            row.Controls.Add(count);
            textRight = count.Left - Brand.S(8);
        }

        int textLeft = time.Right + Brand.S(10);
        var text = new Label { AutoSize = false, AutoEllipsis = true, Text = entry.Text, Font = Brand.Ui(9f, pause ? FontStyle.Italic : FontStyle.Regular), ForeColor = pause ? Palette.Gray.With(0.6) : Palette.White, BackColor = fill, Location = new Point(textLeft, Brand.S(7)), Size = new Size(Math.Max(Brand.S(20), textRight - textLeft), Brand.S(16)), TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        row.Controls.Add(text);

        _content.Controls.Add(row);
        return y + Brand.S(30) + Brand.S(6);
    }

    /// <summary>Ispravak jednog reda kronološkog pregleda — opis, vrijeme i vrsta.</summary>
    private void EditEntry(ChronoRow entry)
    {
        using var dialog = new EntryEditForm(entry, _engine.History);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _engine.UpdateEntries(entry, DayKey, dialog.EntryText, dialog.EntryStart, dialog.EntryEnd, dialog.Kind);
        Rebuild();
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
