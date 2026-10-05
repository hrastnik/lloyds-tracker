using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LloydsTracker;

/// <summary>Pop-up that announces a new version, once per version (mirrors the macOS
/// UpdatePopupController/View). After that the notice stays only in the menu.</summary>
internal sealed class UpdatePopupController
{
    private UpdatePopupForm? _form;

    public bool IsVisible => _form is { IsDisposed: false };

    public void Show(AvailableUpdate update, string currentVersion, Action onDownload)
    {
        if (IsVisible) return;
        var form = new UpdatePopupForm(update, currentVersion,
            onDownload: () => { Close(); onDownload(); },
            onDismiss: Close);
        _form = form;
        form.FormClosed += (_, _) => { if (_form == form) _form = null; };
        form.Appear();
    }

    public void Close()
    {
        if (_form is { IsDisposed: false } f) { _form = null; f.Close(); f.Dispose(); }
        else _form = null;
    }
}

internal sealed class UpdatePopupForm : Form
{
    private readonly Action _onDownload;
    private readonly Action _onDismiss;

    public UpdatePopupForm(AvailableUpdate update, string currentVersion, Action onDownload, Action onDismiss)
    {
        _onDownload = onDownload;
        _onDismiss = onDismiss;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Palette.Black;
        ClientSize = new Size(Brand.S(360), Brand.S(10));

        Build(update, currentVersion);
        // Like isMovableByWindowBackground on macOS.
        FormDrag.Enable(this, this);
    }

    private void Build(AvailableUpdate update, string currentVersion)
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
        logoRow.Controls.AddRange(new Control[] { square, lloyds, tracker });
        Controls.Add(logoRow);
        y += Brand.S(18) + Brand.S(16);

        var title = new Label { AutoSize = true, Text = $"Nova verzija {update.Version}", Font = Brand.Ui(16f, FontStyle.Bold), ForeColor = Palette.White, BackColor = Palette.Black, Location = new Point(pad, y) };
        Controls.Add(title);
        y += title.Height + Brand.S(6);

        string bodyText = $"Imaš verziju {currentVersion}. Novu preuzmi s GitHuba i zamijeni postojeću aplikaciju. Poveznica ostaje i u meniju.";
        var body = new Label { AutoSize = false, Text = bodyText, Font = Brand.Ui(9f), ForeColor = Palette.Gray, BackColor = Palette.Black, Location = new Point(pad, y), Width = innerW };
        body.Height = TextRenderer.MeasureText(body.Text, body.Font, new Size(innerW, int.MaxValue), TextFormatFlags.WordBreak).Height + Brand.S(2);
        Controls.Add(body);
        y += body.Height + Brand.S(16);

        // The arrow stands in for the macOS arrow.down.circle.fill symbol.
        var download = new FlatButton { Text = "↓  Preuzmi", Fill = Palette.Yellow, TextColor = Palette.Black, Font = Brand.Ui(10f, FontStyle.Bold), CornerRadius = 10, BackColor = Palette.Black, Size = new Size(innerW - Brand.S(96), Brand.S(40)), Location = new Point(pad, y) };
        download.Click += (_, _) => _onDownload();
        Controls.Add(download);

        var later = new FlatButton { Text = "Kasnije", TextColor = Palette.Gray, BorderColor = Palette.White.OverBlack(0.2), BorderWidth = 1, CornerRadius = 10, Font = Brand.Ui(10f, FontStyle.Bold), BackColor = Palette.Black, Size = new Size(Brand.S(88), Brand.S(40)), Location = new Point(pad + innerW - Brand.S(88), y) };
        later.Click += (_, _) => _onDismiss();
        Controls.Add(later);
        y += Brand.S(40) + pad;

        ClientSize = new Size(ClientSize.Width, y);

        // Same corner as the startup reminder (top right, above other windows).
        var wa = PromptGeometry.PromptScreen().WorkingArea;
        Location = new Point(wa.Right - Width - Brand.S(24), wa.Top + Brand.S(24));
        Region = new Region(Brand.RoundedRect(new RectangleF(0, 0, Width, Height), Brand.Sf(16)));
    }

    /// <summary>The pop-up doesn't take the keyboard right away — see <see cref="PanelFade"/>.</summary>
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
