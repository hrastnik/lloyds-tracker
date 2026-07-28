using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace LloydsTracker;

/// <summary>Warning shown a minute before the automatic stop of the work day, offering a
/// same-day extension (mirrors the macOS AutoStopWarningController/View). If it's ignored,
/// the engine closes the day itself — so tracking never stays on overnight.</summary>
internal sealed class AutoStopWarningController
{
    private AutoStopWarningForm? _form;

    public bool IsVisible => _form is { IsDisposed: false };

    public void Show(DateTime stopAt, double lead, Action<int> onExtend, Action onStopNow, Action onDismiss)
    {
        if (IsVisible) return;
        var form = new AutoStopWarningForm(stopAt, lead,
            onExtend: minutes => { Close(); onExtend(minutes); },
            onStopNow: () => { Close(); onStopNow(); },
            onDismiss: () => { Close(); onDismiss(); });
        _form = form;
        form.FormClosed += (_, _) => { if (_form == form) _form = null; };
        form.Show();
        form.Activate();
    }

    public void Close()
    {
        if (_form is { IsDisposed: false } f) { _form = null; f.Close(); f.Dispose(); }
        else _form = null;
    }
}

internal sealed class AutoStopWarningForm : Form
{
    /// <summary>Extensions count from the scheduled stop time (16:00 + 30 min → 16:30).</summary>
    private static readonly int[] Options = { 15, 30, 45, 60 };

    private readonly DateTime _stopAt;
    private readonly double _lead;
    private readonly Action<int> _onExtend;
    private readonly Action _onStopNow;
    private readonly Action _onDismiss;

    private readonly WinFormsTimer _tick = new() { Interval = 250 };
    private Label _countdown = null!;
    private Panel _barTrack = null!;
    private Panel _barFill = null!;

    public AutoStopWarningForm(DateTime stopAt, double lead, Action<int> onExtend, Action onStopNow, Action onDismiss)
    {
        _stopAt = stopAt;
        _lead = lead;
        _onExtend = onExtend;
        _onStopNow = onStopNow;
        _onDismiss = onDismiss;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Palette.Black;
        ClientSize = new Size(Brand.S(380), Brand.S(10));

        Build();

        _tick.Tick += (_, _) => UpdateCountdown();
        _tick.Start();
    }

    private void Build()
    {
        int pad = Brand.S(22);
        int innerW = ClientSize.Width - pad * 2;
        int y = pad;

        // Logo row + countdown.
        var logoRow = new Panel { Location = new Point(pad, y), Size = new Size(innerW, Brand.S(18)), BackColor = Palette.Black };
        var square = new Panel { BackColor = Palette.Yellow, Size = new Size(Brand.S(18), Brand.S(18)), Location = new Point(0, 0) };
        var lloyds = new TrackedLabel { Text = "LLOYDS", Font = Brand.Ui(10.5f, FontStyle.Bold), ForeColor = Palette.White, Tracking = 2f, BackColor = Palette.Black };
        lloyds.Location = new Point(square.Right + Brand.S(8), (Brand.S(18) - lloyds.Height) / 2);
        var tracker = new TrackedLabel { Text = "TRACKER", Font = Brand.Ui(10.5f), ForeColor = Palette.Gray, Tracking = 2f, BackColor = Palette.Black };
        tracker.Location = new Point(lloyds.Right + Brand.S(6), (Brand.S(18) - tracker.Height) / 2);
        _countdown = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleRight, Text = Fmt.Countdown(_lead), Font = Brand.Mono(9.5f, FontStyle.Bold), ForeColor = Palette.Yellow, BackColor = Palette.Black, Width = Brand.S(60), Height = Brand.S(18) };
        _countdown.Location = new Point(innerW - _countdown.Width, 0);
        logoRow.Controls.AddRange(new Control[] { square, lloyds, tracker, _countdown });
        Controls.Add(logoRow);
        y += Brand.S(18) + Brand.S(12);

        // Bar that drains until the stop — the remaining time at a glance.
        _barTrack = new Panel { BackColor = Palette.White.OverBlack(0.12), Location = new Point(pad, y), Size = new Size(innerW, Brand.S(4)) };
        _barFill = new Panel { BackColor = Palette.Yellow, Location = new Point(0, 0), Size = new Size(innerW, Brand.S(4)) };
        _barTrack.Controls.Add(_barFill);
        Controls.Add(_barTrack);
        y += Brand.S(4) + Brand.S(14);

        var title = new Label { AutoSize = true, Text = "Zaustavljam tracking", Font = Brand.Ui(16f, FontStyle.Bold), ForeColor = Palette.White, BackColor = Palette.Black, Location = new Point(pad, y) };
        Controls.Add(title);
        y += title.Height + Brand.S(6);

        string subtitleText = $"Radni dan se automatski zatvara u {Fmt.Hhmm(_stopAt)}. Ako još radiš, produži — inače dobiješ pregled dana i tracking se zaustavlja.";
        var subtitle = new Label { AutoSize = false, Text = subtitleText, Font = Brand.Ui(9f), ForeColor = Palette.Gray, BackColor = Palette.Black, Location = new Point(pad, y), Width = innerW };
        subtitle.Height = TextRenderer.MeasureText(subtitleText, subtitle.Font, new Size(innerW, int.MaxValue), TextFormatFlags.WordBreak).Height + Brand.S(2);
        Controls.Add(subtitle);
        y += subtitle.Height + Brand.S(14);

        var section = new TrackedLabel { Text = "PRODUŽI — SAMO ZA DANAS", Font = Brand.Ui(8.5f, FontStyle.Bold), ForeColor = Palette.Gray.With(0.8), Tracking = 1.5f, BackColor = Palette.Black, Location = new Point(pad, y) };
        Controls.Add(section);
        y += section.Height + Brand.S(8);

        int gap = Brand.S(8);
        int chipW = (innerW - gap * (Options.Length - 1)) / Options.Length;
        for (int i = 0; i < Options.Length; i++)
        {
            int minutes = Options[i];
            var chip = new FlatButton
            {
                Text = ExtendLabel(minutes),
                SubText = Fmt.Hhmm(_stopAt.AddMinutes(minutes)),
                SubFont = Brand.Mono(7.5f),
                SubColor = Palette.Yellow.OverBlack(0.6),
                TextColor = Palette.Yellow,
                Fill = Palette.Yellow.With(0.12),
                BorderColor = Palette.Yellow.With(0.5),
                BorderWidth = 1,
                CornerRadius = 8,
                Font = Brand.Ui(10f, FontStyle.Bold),
                BackColor = Palette.Black,
                Size = new Size(chipW, Brand.S(42)),
                Location = new Point(pad + i * (chipW + gap), y),
            };
            chip.Click += (_, _) => _onExtend(minutes);
            Controls.Add(chip);
        }
        y += Brand.S(42) + Brand.S(12);

        var stopNow = new FlatButton
        {
            Text = "Zaustavi sad",
            TextColor = Palette.Gray,
            BorderColor = Palette.White.OverBlack(0.2),
            BorderWidth = 1,
            CornerRadius = 8,
            Font = Brand.Ui(10f, FontStyle.Bold),
            BackColor = Palette.Black,
            Size = new Size(innerW, Brand.S(34)),
            Location = new Point(pad, y),
        };
        stopNow.Click += (_, _) => _onStopNow();
        Controls.Add(stopNow);
        y += Brand.S(34) + pad;

        ClientSize = new Size(ClientSize.Width, y);

        // Bottom-right corner — the prompt sits top-right, so the two never overlap.
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - Brand.S(24), wa.Bottom - Height - Brand.S(24));
        Region = new Region(Brand.RoundedRect(new RectangleF(0, 0, Width, Height), Brand.Sf(16)));

        UpdateCountdown();
    }

    internal static string ExtendLabel(int minutes) => minutes >= 60 ? "+1 h" : $"+{minutes} min";

    private void UpdateCountdown()
    {
        if (IsDisposed) return;
        double remaining = Math.Max(0, (_stopAt - DateTime.Now).TotalSeconds);
        _countdown.Text = Fmt.Countdown(remaining);
        double fraction = _lead > 0 ? Math.Clamp(remaining / _lead, 0, 1) : 0;
        _barFill.Width = (int)Math.Round(_barTrack.Width * fraction);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Stop();
            _tick.Dispose();
        }
        base.Dispose(disposing);
    }
}
