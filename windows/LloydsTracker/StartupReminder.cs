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

    /// <summary><paramref name="backfillFrom"/> (a start of the work day that has already passed)
    /// adds the choice between starting from that time or only from now. <paramref name="onStart"/>
    /// gets only whether the backfill was chosen — the exact time is computed by the engine at
    /// click time, because the window waits for an answer and may stay up overnight, when a
    /// remembered date would move the start back to yesterday.</summary>
    public void Show(string dayTitle, DateTime? backfillFrom, Action<bool> onStart, Action onDismiss)
    {
        if (IsVisible) return;
        var form = new StartupReminderForm(dayTitle, backfillFrom,
            onStart: useBackfill => { Close(); onStart(useBackfill); },
            onDismiss: () => { Close(); onDismiss(); });
        _form = form;
        form.FormClosed += (_, _) => { if (_form == form) Forget(); };
        form.Show();
        form.Activate();
        ShownAt = DateTime.Now;
        ShownBackfillFrom = backfillFrom;
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
    /// <summary>The parameter is "with backfill" (true) or "only from now" (false).</summary>
    private readonly Action<bool> _onStart;
    private readonly Action _onDismiss;
    /// <summary>Set → the morning backfill (start from the beginning of the work day) is offered too.</summary>
    private readonly DateTime? _backfillFrom;

    public StartupReminderForm(string dayTitle, DateTime? backfillFrom, Action<bool> onStart, Action onDismiss)
    {
        _onStart = onStart;
        _onDismiss = onDismiss;
        _backfillFrom = backfillFrom;

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
        var day = new Label { AutoSize = true, Text = dayTitle, Font = Brand.Ui(8f), ForeColor = Palette.Gray.With(0.7), BackColor = Palette.Black };
        day.Location = new Point(innerW - day.PreferredWidth, (Brand.S(18) - day.Height) / 2);
        day.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        logoRow.Controls.AddRange(new Control[] { square, lloyds, tracker, day });
        Controls.Add(logoRow);
        y += Brand.S(18) + Brand.S(16);

        var title = new Label { AutoSize = true, Text = "Novi radni dan?", Font = Brand.Ui(16f, FontStyle.Bold), ForeColor = Palette.White, BackColor = Palette.Black, Location = new Point(pad, y) };
        Controls.Add(title);
        y += title.Height + Brand.S(6);

        string subtitleText = _backfillFrom is DateTime startFrom
            ? $"Radni dan počinje u {Fmt.Hhmm(startFrom)}, a tracking još nije pokrenut. Mogu ga voditi od tada (pa te prvi prompt pita i za jutro) ili tek od sada."
            : "Tracking još nije pokrenut. Klikni Start da počneš bilježiti vrijeme.";
        var subtitle = new Label { AutoSize = false, Text = subtitleText, Font = Brand.Ui(9f), ForeColor = Palette.Gray, BackColor = Palette.Black, Location = new Point(pad, y), Width = innerW };
        subtitle.Height = TextRenderer.MeasureText(subtitle.Text, subtitle.Font, new Size(innerW, int.MaxValue), TextFormatFlags.WordBreak).Height + Brand.S(2);
        Controls.Add(subtitle);
        y += subtitle.Height + Brand.S(16);

        // With a backfill the primary button starts from the beginning of the work day — that's
        // the reason the pop-up appeared at the set time in the first place.
        string startText = _backfillFrom is DateTime backfill ? $"Start od {Fmt.Hhmm(backfill)}" : "Start — počni radni dan";
        var start = new FlatButton { Text = startText, Fill = Palette.Yellow, TextColor = Palette.Black, Font = Brand.Ui(10f, FontStyle.Bold), CornerRadius = 10, BackColor = Palette.Black, Size = new Size(innerW - Brand.S(96), Brand.S(40)), Location = new Point(pad, y) };
        start.Click += (_, _) => _onStart(_backfillFrom != null);
        Controls.Add(start);

        var later = new FlatButton { Text = "Kasnije", TextColor = Palette.Gray, BorderColor = Palette.White.OverBlack(0.2), BorderWidth = 1, CornerRadius = 10, Font = Brand.Ui(10f, FontStyle.Bold), BackColor = Palette.Black, Size = new Size(Brand.S(88), Brand.S(40)), Location = new Point(pad + innerW - Brand.S(88), y) };
        later.Click += (_, _) => _onDismiss();
        Controls.Add(later);
        y += Brand.S(40);

        if (_backfillFrom != null)
        {
            y += Brand.S(10);
            var startNow = new FlatButton { Text = "Počni tek od sada", TextColor = Palette.Gray, BorderColor = Palette.White.OverBlack(0.2), BorderWidth = 1, CornerRadius = 10, Font = Brand.Ui(9.5f, FontStyle.Bold), BackColor = Palette.Black, Size = new Size(innerW, Brand.S(36)), Location = new Point(pad, y) };
            startNow.Click += (_, _) => _onStart(false);
            Controls.Add(startNow);
            y += Brand.S(36);
        }
        y += pad;

        ClientSize = new Size(ClientSize.Width, y);

        var wa = PromptGeometry.PromptScreen().WorkingArea;
        Location = new Point(wa.Right - Width - Brand.S(24), wa.Top + Brand.S(24));
        Region = new Region(Brand.RoundedRect(new RectangleF(0, 0, Width, Height), Brand.Sf(16)));
    }

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
