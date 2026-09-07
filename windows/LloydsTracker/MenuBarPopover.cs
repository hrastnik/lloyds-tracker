using System.Drawing;
using System.Windows.Forms;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace LloydsTracker;

/// <summary>The popover shown from the tray icon — mirrors the SwiftUI MenuBarView
/// (header, status + controls, today's entries, footer).</summary>
internal sealed class MenuBarPopover : Form
{
    // Scaled from the 96-DPI design (Brand.Dpi is set at startup). Everything below derives
    // from Width_ or a Brand.S(...) literal, so the whole popover scales crisply.
    private readonly int Width_ = Brand.S(340);
    private const int MaxVisibleEntries = 8;

    private readonly TrackerEngine _engine;
    private readonly Action _openSummary;
    private readonly Action _openSettings;
    private readonly Action _quit;

    private readonly WinFormsTimer _tick = new() { Interval = 1000 };
    /// <summary>Tooltip za gumbe (macOS `.help(…)`).</summary>
    private readonly ToolTip _tips = new();
    private Label? _countdown;
    private bool _suppressHide;

    public MenuBarPopover(TrackerEngine engine, Action openSummary, Action openSettings, Action quit)
    {
        _engine = engine;
        _openSummary = openSummary;
        _openSettings = openSettings;
        _quit = quit;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Palette.Black;
        Width = Width_;

        _tick.Tick += (_, _) => UpdateCountdown();
        _engine.Changed += OnEngineChanged;
    }

    private void OnEngineChanged()
    {
        if (Visible) RebuildContent();
    }

    public void Toggle()
    {
        if (Visible) HidePopover();
        else ShowPopover();
    }

    public void ShowPopover()
    {
        RebuildContent();
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - Brand.S(8), wa.Bottom - Height - Brand.S(8));
        Show();
        Activate();
        _tick.Start();
    }

    public void HidePopover()
    {
        _tick.Stop();
        Hide();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_suppressHide) HidePopover();
    }

    // MARK: - Content

    private void RebuildContent()
    {
        SuspendLayout();
        foreach (Control c in Controls.Cast<Control>().ToList()) c.Dispose();
        Controls.Clear();
        _countdown = null;

        int y = 0;
        y = BuildHeader(y);
        y = Divider(y);
        y = BuildStatus(y);
        y = Divider(y);
        y = BuildEntries(y);
        y = Divider(y);
        y = BuildFooter(y);

        ClientSize = new Size(Width_, y);
        ResumeLayout(true);
    }

    private int Divider(int y)
    {
        var d = new Panel { BackColor = Palette.White.OverBlack(0.1), Location = new Point(0, y), Size = new Size(Width_, Brand.S(1)) };
        Controls.Add(d);
        return y + Brand.S(1);
    }

    private int BuildHeader(int y)
    {
        int top = y + Brand.S(12);
        var square = new Panel { BackColor = Palette.Yellow, Size = new Size(Brand.S(18), Brand.S(18)), Location = new Point(Brand.S(16), top) };
        Controls.Add(square);

        var lloyds = new TrackedLabel { Text = "LLOYDS", Font = Brand.Ui(10.5f, FontStyle.Bold), ForeColor = Palette.White, Tracking = 2f, BackColor = Palette.Black };
        lloyds.Location = new Point(square.Right + Brand.S(8), top + (Brand.S(18) - lloyds.Height) / 2);
        Controls.Add(lloyds);

        var tracker = new TrackedLabel { Text = "TRACKER", Font = Brand.Ui(10.5f), ForeColor = Palette.Gray, Tracking = 2f, BackColor = Palette.Black };
        tracker.Location = new Point(lloyds.Right + Brand.S(6), top + (Brand.S(18) - tracker.Height) / 2);
        Controls.Add(tracker);

        var day = new Label { AutoSize = true, Text = Fmt.DayTitle(DateTime.Now), Font = Brand.Ui(8f), ForeColor = Palette.Gray.With(0.7), BackColor = Palette.Black };
        day.Location = new Point(Width_ - Brand.S(16) - day.PreferredWidth, top + (Brand.S(18) - day.Height) / 2);
        day.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(day);

        return top + Brand.S(18) + Brand.S(12);
    }

    private int BuildStatus(int y)
    {
        int top = y + Brand.S(12);

        var dot = new Dot { Color = StatusColor(), BackColor = Palette.Black, Size = new Size(Brand.S(8), Brand.S(8)), Location = new Point(Brand.S(16), top + Brand.S(3)) };
        Controls.Add(dot);

        var status = new Label { AutoSize = true, Text = _engine.StatusText, Font = Brand.Ui(9.5f, FontStyle.Bold), ForeColor = Palette.White, BackColor = Palette.Black };
        status.Location = new Point(dot.Right + Brand.S(8), top);
        Controls.Add(status);

        if (_engine.IsTracking && _engine.PauseUntil == null && _engine.NextPromptAt != null)
        {
            _countdown = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Font = Brand.Mono(8.5f), ForeColor = Palette.Yellow, BackColor = Palette.Black, Width = Brand.S(120), Height = status.Height };
            _countdown.Location = new Point(Width_ - Brand.S(16) - _countdown.Width, top);
            _countdown.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(_countdown);
            UpdateCountdown();
        }

        int rowY = top + status.Height + Brand.S(6);

        if (_engine.AutoStopText is string autoStop)
        {
            var info = new Label { AutoSize = true, Text = autoStop, Font = Brand.Ui(8f), ForeColor = Palette.Gray.With(0.7), BackColor = Palette.Black, Location = new Point(Brand.S(16), rowY) };
            Controls.Add(info);
            rowY += info.Height + Brand.S(2);
        }
        rowY += Brand.S(4);

        // "Zapiši sada" — prompt na zahtjev, iznad Pauziraj / Završi dan.
        if (_engine.CanPromptNow)
        {
            var now = new FlatButton
            {
                Text = "✎  Zapiši sada", TextColor = Palette.Yellow, Fill = Palette.Yellow.With(0.12),
                BorderColor = Palette.Yellow.With(0.5), BorderWidth = 1, CornerRadius = 8,
                Font = Brand.Ui(9.5f, FontStyle.Bold), BackColor = Palette.Black,
                Size = new Size(Width_ - Brand.S(32), Brand.S(30)), Location = new Point(Brand.S(16), rowY),
            };
            _tips.SetToolTip(now, "Zapiši period do sada i (ako želiš) reci čime nastavljaš");
            // Popover se ne zatvara sam kad se klikne gumb — bez ovoga bi prompt iskočio
            // ispod njega.
            now.Click += (_, _) => { HidePopover(); _engine.ManualPrompt(); };
            Controls.Add(now);
            rowY += Brand.S(30) + Brand.S(8);
        }

        if (_engine.IsTracking)
        {
            if (_engine.PauseUntil == null)
            {
                var pause = new FlatButton
                {
                    Text = "Pauziraj", TextColor = Palette.Gray, Align = ContentAlignment.MiddleLeft,
                    Font = Brand.Ui(9.5f, FontStyle.Bold), BackColor = Palette.Black, Size = new Size(Brand.S(90), Brand.S(26)),
                    Location = new Point(Brand.S(16), rowY),
                };
                var menu = BuildPauseMenu();
                pause.Click += (_, _) => { _suppressHide = true; menu.Show(pause, new Point(0, pause.Height)); };
                Controls.Add(pause);
            }
            else
            {
                var resume = Pill("Nastavi", Palette.Yellow, new Point(Brand.S(16), rowY));
                resume.Click += (_, _) => _engine.Resume();
                Controls.Add(resume);
            }

            var stop = Pill("Završi dan", Palette.StopRed, Point.Empty);
            stop.Location = new Point(Width_ - Brand.S(16) - stop.Width, rowY);
            stop.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            stop.Click += (_, _) => { HidePopover(); _engine.Stop(); };
            Controls.Add(stop);

            rowY += Brand.S(26);
        }
        else
        {
            var start = new FlatButton
            {
                Text = "Start — počni radni dan", Fill = Palette.Yellow, TextColor = Palette.Black,
                Font = Brand.Ui(9.5f, FontStyle.Bold), CornerRadius = 8, BackColor = Palette.Black,
                Size = new Size(Width_ - Brand.S(32), Brand.S(34)), Location = new Point(Brand.S(16), rowY),
            };
            start.Click += (_, _) => _engine.Start();
            Controls.Add(start);
            rowY += Brand.S(34);
        }

        return rowY + Brand.S(12);
    }

    private ContextMenuStrip BuildPauseMenu()
    {
        var menu = new ContextMenuStrip { RenderMode = ToolStripRenderMode.System };
        void Add(string text, int? minutes) => menu.Items.Add(text, null, (_, _) => _engine.Pause(minutes));
        Add("15 minuta", 15);
        Add("30 minuta", 30);
        Add("1 sat", 60);
        Add("Do nastavka", null);
        menu.Closed += (_, _) =>
        {
            _suppressHide = false;
            if (!ContainsFocus) HidePopover();
        };
        return menu;
    }

    private FlatButton Pill(string text, Color color, Point location) => new()
    {
        Text = text,
        TextColor = color,
        Fill = color.With(0.12),
        BorderColor = color.With(0.5),
        BorderWidth = 1,
        CornerRadius = 8,
        Font = Brand.Ui(9.5f, FontStyle.Bold),
        BackColor = Palette.Black,
        Size = new Size(TextRenderer.MeasureText(text, Brand.Ui(9.5f, FontStyle.Bold)).Width + Brand.S(28), Brand.S(26)),
        Location = location,
    };

    private int BuildEntries(int y)
    {
        int top = y + Brand.S(10);

        var label = new TrackedLabel { Text = "DANAS", Font = Brand.Ui(8f, FontStyle.Bold), ForeColor = Palette.Gray, Tracking = 1.5f, BackColor = Palette.Black };
        label.Location = new Point(Brand.S(16), top);
        Controls.Add(label);

        double total = Summarize.WorkTotal(_engine.Entries);
        if (total > 0)
        {
            var totalLabel = new Label { AutoSize = true, Text = $"ukupno {Fmt.Dur(total)}", Font = Brand.Ui(8f, FontStyle.Bold), ForeColor = Palette.Yellow, BackColor = Palette.Black };
            totalLabel.Location = new Point(Width_ - Brand.S(16) - totalLabel.PreferredWidth, top);
            totalLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(totalLabel);
        }

        int rowY = top + label.Height + Brand.S(6);

        if (_engine.Entries.Count == 0)
        {
            var empty = new Label { AutoSize = true, Text = "Još nema unosa.", Font = Brand.Ui(9f), ForeColor = Palette.Gray.With(0.6), BackColor = Palette.Black, Location = new Point(Brand.S(16), rowY) };
            Controls.Add(empty);
            return rowY + empty.Height + Brand.S(10);
        }

        int hidden = _engine.Entries.Count - MaxVisibleEntries;
        var visible = _engine.Entries.Skip(Math.Max(0, _engine.Entries.Count - MaxVisibleEntries)).Reverse().ToList();
        foreach (var entry in visible)
        {
            rowY = BuildEntryRow(entry, rowY);
        }

        if (hidden > 0)
        {
            var more = new FlatButton
            {
                Text = $"… i još {hidden} ranijih — Pregled dana", TextColor = Palette.Gray.With(0.7),
                Font = Brand.Ui(8f, FontStyle.Underline), Align = ContentAlignment.MiddleLeft,
                BackColor = Palette.Black, Size = new Size(Width_ - Brand.S(32), Brand.S(18)), Location = new Point(Brand.S(16), rowY + Brand.S(2)),
            };
            more.Click += (_, _) => { HidePopover(); _openSummary(); };
            Controls.Add(more);
            rowY += Brand.S(20);
        }

        return rowY + Brand.S(10);
    }

    private int BuildEntryRow(Entry entry, int y)
    {
        bool pause = entry.Kind == EntryKind.Pause;
        var time = new Label { AutoSize = true, Text = $"{Fmt.Hhmm(entry.Start)}–{Fmt.Hhmm(entry.End)}", Font = Brand.Mono(8f), ForeColor = Palette.Gray.With(pause ? 0.5 : 0.9), BackColor = Palette.Black, Location = new Point(Brand.S(16), y + Brand.S(2)) };
        Controls.Add(time);

        var dur = new Label { AutoSize = true, Text = Fmt.Dur(entry.Duration), Font = Brand.Mono(8f), ForeColor = Palette.Gray.With(0.7), BackColor = Palette.Black };
        dur.Location = new Point(Width_ - Brand.S(16) - dur.PreferredWidth, y + Brand.S(2));
        dur.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(dur);

        int textLeft = time.Right + Brand.S(8);
        int textRight = dur.Left - Brand.S(8);
        var text = new Label
        {
            AutoSize = false, AutoEllipsis = true, Text = entry.Text,
            Font = Brand.Ui(9f, pause ? FontStyle.Italic : FontStyle.Regular),
            ForeColor = pause ? Palette.Gray.With(0.5) : Palette.White, BackColor = Palette.Black,
            Location = new Point(textLeft, y + Brand.S(2)), Size = new Size(Math.Max(Brand.S(20), textRight - textLeft), time.Height),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Controls.Add(text);

        return y + Math.Max(time.Height, text.Height) + Brand.S(4);
    }

    private int BuildFooter(int y)
    {
        int top = y + Brand.S(10);
        var summary = FooterLink("Pregled dana", new Point(Brand.S(16), top));
        summary.Click += (_, _) => { HidePopover(); _openSummary(); };
        Controls.Add(summary);

        var settings = FooterLink("Postavke…", Point.Empty);
        settings.Location = new Point((Width_ - settings.Width) / 2, top);
        settings.Click += (_, _) => { HidePopover(); _openSettings(); };
        Controls.Add(settings);

        var quit = FooterLink("Izlaz", Point.Empty);
        quit.Location = new Point(Width_ - Brand.S(16) - quit.Width, top);
        quit.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        quit.Click += (_, _) => _quit();
        Controls.Add(quit);

        return top + summary.Height + Brand.S(10);
    }

    private FlatButton FooterLink(string text, Point location)
    {
        var font = Brand.Ui(9f);
        return new FlatButton
        {
            Text = text, TextColor = Palette.Gray, Font = font, BackColor = Palette.Black,
            Size = new Size(TextRenderer.MeasureText(text, font).Width + Brand.S(8), Brand.S(20)), Location = location,
        };
    }

    private void UpdateCountdown()
    {
        if (_countdown == null || _countdown.IsDisposed) return;
        if (_engine.IsTracking && _engine.PauseUntil == null && _engine.NextPromptAt is DateTime next)
            _countdown.Text = $"prompt za {Fmt.Countdown((next - DateTime.Now).TotalSeconds)}";
    }

    private Color StatusColor()
    {
        if (!_engine.IsTracking) return Palette.Gray.With(0.5);
        if (_engine.PauseUntil != null) return Palette.Orange;
        if (_engine.AwaitingReturnSince != null) return Palette.Blue;
        return Palette.Yellow;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.Changed -= OnEngineChanged;
            _tick.Dispose();
        }
        base.Dispose(disposing);
    }
}
