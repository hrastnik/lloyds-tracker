using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LloydsTracker;

/// <summary>Pop-up shown at launch and at the set start of the work day, to remind you to
/// start the day (mirrors the macOS StartupReminderController/View). Same floating panel as
/// the prompt.</summary>
internal sealed class StartupReminderController
{
    private StartupReminderForm? _form;

    public bool IsVisible => _form is { IsDisposed: false };

    /// <summary>When the reminder was shown and with which backfill offer — that's how an open
    /// window is recognised as stale (it stayed up overnight, or the work day has only just
    /// started).</summary>
    public DateTime? ShownAt { get; private set; }
    public DateTime? ShownBackfillFrom { get; private set; }

    /// <summary><paramref name="startOptions"/> (from a start of the work day that has already
    /// passed — or the end of today's last entry — up to the start of the current interval, along
    /// the interval grid) adds ‹ › arrows that move the start. <paramref name="onStart"/> gets the chosen time, or null to start from now.</summary>
    public void Show(string dayTitle, IReadOnlyList<DateTime> startOptions, Action<DateTime?> onStart, Action onDismiss)
    {
        if (IsVisible) return;
        var form = new StartupReminderForm(dayTitle, startOptions,
            onStart: from => { Close(); onStart(from); },
            onDismiss: () => { Close(); onDismiss(); });
        _form = form;
        // Closed by the system (Alt+F4) rather than by us — same as "Kasnije". Close() forgets
        // the form first, so this doesn't fire twice.
        form.FormClosed += (_, _) => { if (_form == form) { Forget(); onDismiss(); } };
        form.Appear();
        ShownAt = DateTime.Now;
        ShownBackfillFrom = startOptions.Count > 0 ? startOptions[0] : null;
    }

    /// <summary>The offer of starts holds for the interval the window was shown in — if it stays
    /// up for hours, the latest start lags behind (shown at 8:40, at 11:00 it still offers 8:30 at
    /// most). The engine sends it a fresh offer on each new interval; the chosen time stays chosen
    /// if it's still on offer. Only when the window already offers a backfill and the new offer
    /// isn't empty — the backfill appearing and a window left overnight are handled by the engine
    /// replacing the window.</summary>
    public void UpdateStartOptions(IReadOnlyList<DateTime> options)
    {
        if (_form is not { IsDisposed: false } f || ShownBackfillFrom == null || options.Count == 0) return;
        if (f.UpdateStartOptions(options)) ShownBackfillFrom = options[0];
    }

    public void Close()
    {
        if (_form is { IsDisposed: false } f) { Forget(); f.Close(); f.Dispose(); }
        else Forget();
    }

    private void Forget()
    {
        _form = null;
        ShownAt = null;
        ShownBackfillFrom = null;
    }
}

internal sealed class StartupReminderForm : Form
{
    /// <summary>The parameter is the chosen time, or null to start from now.</summary>
    private readonly Action<DateTime?> _onStart;
    private readonly Action _onDismiss;
    /// <summary>Not empty → the morning backfill is offered too: the arrows move the start from the
    /// start of the work day (or the end of today's last entry) up to the start of the current
    /// interval.</summary>
    private IReadOnlyList<DateTime> _startOptions;
    /// <summary>Index into <see cref="_startOptions"/>. The default is the earliest start (the start
    /// of the work day, or the end of today's last entry) — that's the reason the pop-up appeared
    /// at the set time in the first place.</summary>
    private int _selection;
    private Label _subtitle = null!;
    private FlatButton _start = null!;
    /// <summary>Only with a backfill offer.</summary>
    private FlatButton? _stepBack, _stepForward;

    public StartupReminderForm(string dayTitle, IReadOnlyList<DateTime> startOptions, Action<DateTime?> onStart, Action onDismiss)
    {
        _onStart = onStart;
        _onDismiss = onDismiss;
        _startOptions = startOptions;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Palette.Black;
        ClientSize = new Size(Brand.S(360), Brand.S(10));

        Build(dayTitle);
    }

    private void Build(string dayTitle)
    {
        int pad = Brand.S(22);
        int innerW = ClientSize.Width - pad * 2;
        int y = pad;

        var logoRow = new Panel { Location = new Point(pad, y), Size = new Size(innerW, Brand.S(18)), BackColor = Palette.Black };
        var square = new Panel { BackColor = Palette.Yellow, Size = new Size(Brand.S(18), Brand.S(18)), Location = new Point(0, 0) };
        var lloyds = new TrackedLabel { Text = "LLOYDS", Font = Brand.Ui(10.5f, FontStyle.Bold), ForeColor = Palette.White, Tracking = 2f, BackColor = Palette.Black };
        lloyds.Location = new Point(square.Right + Brand.S(8), (Brand.S(18) - lloyds.Height) / 2);
        var tracker = new TrackedLabel { Text = "TRACKER", Font = Brand.Ui(10.5f), ForeColor = Palette.Gray, Tracking = 2f, BackColor = Palette.Black };
        tracker.Location = new Point(lloyds.Right + Brand.S(6), (Brand.S(18) - tracker.Height) / 2);
        var day = new Label { AutoSize = true, Text = dayTitle, Font = Brand.Ui(8f), ForeColor = Palette.Gray.OverBlack(0.7), BackColor = Palette.Black };
        Brand.FitDayTitle(day, innerW - tracker.Right - Brand.S(12));
        day.Location = new Point(innerW - day.PreferredWidth, (Brand.S(18) - day.Height) / 2);
        day.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        logoRow.Controls.AddRange(new Control[] { square, lloyds, tracker, day });
        Controls.Add(logoRow);
        y += Brand.S(18) + Brand.S(16);

        var title = new Label { AutoSize = true, Text = "Novi radni dan?", Font = Brand.Ui(16f, FontStyle.Bold), ForeColor = Palette.White, BackColor = Palette.Black, Location = new Point(pad, y) };
        Controls.Add(title);
        y += title.Height + Brand.S(6);

        var subtitle = new Label { AutoSize = false, Text = SubtitleText(), Font = Brand.Ui(9f), ForeColor = Palette.Gray, BackColor = Palette.Black, Location = new Point(pad, y), Width = innerW };
        subtitle.Height = TextRenderer.MeasureText(subtitle.Text, subtitle.Font, new Size(innerW, int.MaxValue), TextFormatFlags.WordBreak).Height + Brand.S(2);
        Controls.Add(subtitle);
        _subtitle = subtitle;
        y += subtitle.Height + Brand.S(16);

        // The time sits in the Start button itself and ‹ › move it by one interval — one click for
        // the default, no separate picker.
        int h = Brand.S(40);
        int left = pad;
        int right = pad + innerW - Brand.S(88) - Brand.S(10);
        FlatButton? back = null, forward = null;
        if (_startOptions.Count > 0)
        {
            back = StepButton("‹", new Point(left, y), h);
            forward = StepButton("›", new Point(right - back.Width, y), h);
            left = back.Right + Brand.S(2);
            right = forward.Left - Brand.S(2);
        }

        var start = new FlatButton { Focusable = false, Text = StartTitle(), Fill = Palette.Yellow, TextColor = Palette.Black, Font = Brand.Ui(10f, FontStyle.Bold), CornerRadius = 10, BackColor = Palette.Black, Size = new Size(right - left, h), Location = new Point(left, y) };
        start.Click += (_, _) => _onStart(SelectedStart());
        Controls.Add(start);
        _start = start;

        if (back is FlatButton stepBack && forward is FlatButton stepForward)
        {
            stepBack.Click += (_, _) => { if (_selection > 0) { _selection--; ShowSelection(); } };
            stepForward.Click += (_, _) => { if (_selection < _startOptions.Count - 1) { _selection++; ShowSelection(); } };
            Controls.Add(stepBack);
            Controls.Add(stepForward);
            _stepBack = stepBack;
            _stepForward = stepForward;
            ShowSelection();
        }

        var later = new FlatButton { Focusable = false, Text = "Kasnije", TextColor = Palette.Gray, BorderColor = Palette.White.OverBlack(0.2), BorderWidth = 1, CornerRadius = 10, Font = Brand.Ui(10f, FontStyle.Bold), BackColor = Palette.Black, Size = new Size(Brand.S(88), Brand.S(40)), Location = new Point(pad + innerW - Brand.S(88), y) };
        later.Click += (_, _) => _onDismiss();
        Controls.Add(later);
        y += Brand.S(40);
        y += pad;

        ClientSize = new Size(ClientSize.Width, y);

        var wa = PromptGeometry.PromptScreen().WorkingArea;
        Location = new Point(wa.Right - Width - Brand.S(24), wa.Top + Brand.S(24));
        Region = Brand.RoundedRegion(Size, Brand.Sf(16));
    }

    private void ShowSelection()
    {
        _start.Text = StartTitle();
        _start.Invalidate();
        if (_stepBack is FlatButton back && _stepForward is FlatButton forward)
        {
            SetStepEnabled(back, _selection > 0);
            SetStepEnabled(forward, _selection < _startOptions.Count - 1);
        }
    }

    /// <summary>See <see cref="StartupReminderController.UpdateStartOptions"/>. Only swaps the
    /// offer of a window that already has the arrows; returns whether anything changed.</summary>
    public bool UpdateStartOptions(IReadOnlyList<DateTime> options)
    {
        if (_stepBack == null || options.Count == 0 || options.SequenceEqual(_startOptions)) return false;
        int at = SelectedStart() is DateTime selected ? IndexOf(options, selected) : -1;
        _selection = at >= 0 ? at : Math.Min(_selection, options.Count - 1);
        _startOptions = options;
        // The subtitle doesn't depend on the offered times — it keeps its height.
        _subtitle.Text = SubtitleText();
        ShowSelection();
        return true;
    }

    private static int IndexOf(IReadOnlyList<DateTime> options, DateTime value)
    {
        for (int i = 0; i < options.Count; i++)
            if (options[i] == value) return i;
        return -1;
    }

    private string SubtitleText() => _startOptions.Count > 0
        ? "Tracking još nije pokrenut. Strelicama odaberi od kad radiš — nakon Starta te prompt odmah pita za propušteno vrijeme."
        : "Tracking još nije pokrenut. Klikni Start da počneš bilježiti vrijeme.";

    private DateTime? SelectedStart()
        => _selection >= 0 && _selection < _startOptions.Count ? _startOptions[_selection] : null;

    private string StartTitle()
        => SelectedStart() is DateTime from ? $"Start od {Fmt.Hhmm(from)}" : "Start — počni radni dan";

    /// <summary>Arrow next to the Start button — moves the start by one interval.</summary>
    private static FlatButton StepButton(string glyph, Point location, int height)
        => new() { Focusable = false, Text = glyph, Fill = Palette.Yellow, TextColor = Palette.Black, Font = Brand.Ui(12f, FontStyle.Bold), CornerRadius = 10, BackColor = Palette.Black, Size = new Size(Brand.S(30), height), Location = location };

    private static void SetStepEnabled(FlatButton button, bool enabled)
    {
        button.Enabled = enabled;
        button.Fill = enabled ? Palette.Yellow : Palette.Yellow.OverBlack(0.35);
        // Black at 50 % over the dimmed yellow, flattened — GDI text doesn't blend.
        button.TextColor = enabled ? Palette.Black : Palette.Yellow.OverBlack(0.175);
        button.Cursor = enabled ? Cursors.Hand : Cursors.Default;
        button.Invalidate();
    }

    /// <summary>Pop-up ne uzima tipkovnicu odmah — vidi <see cref="PanelFade"/>.</summary>
    protected override bool ShowWithoutActivation => true;

    public void Appear() => PanelFade.Appear(this);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { _onDismiss(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Palette.Yellow.With(0.35), 1f);
        using var path = Brand.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Brand.Sf(16));
        e.Graphics.DrawPath(pen, path);
    }
}
