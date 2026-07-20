using System.Drawing;
using System.Windows.Forms;

namespace LloydsTracker;

/// <summary>Settings window — mirrors the SwiftUI SettingsView sections and controls.</summary>
internal sealed class SettingsForm : Form
{
    private readonly TrackerEngine _engine;
    private readonly Panel _stack;
    private int _y;

    private ComboBox _idleThreshold = null!;
    private Label _launchStatus = null!;

    public SettingsForm(TrackerEngine engine)
    {
        _engine = engine;
        Text = "Postavke";
        ClientSize = new Size(460, 560);
        MinimumSize = new Size(460, 300);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Palette.Black;
        Font = Brand.Ui(9.5f);

        _stack = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Palette.Black };
        Controls.Add(_stack);

        _y = 16;
        BuildPromptSection();
        BuildIdleSection();
        BuildHistorySection();
        BuildSystemSection();
        BuildDataSection();
    }

    // MARK: - Sections

    private void BuildPromptSection()
    {
        SectionHeader("PROMPTANJE");
        LabeledCombo("Interval", new[] { 5, 10, 15, 20, 30, 45, 60 }, _engine.Settings.IntervalMinutes, " min",
            v => _engine.MutateSettings(s => s.IntervalMinutes = v));

        var styleValues = new[] { PromptStyle.Floating, PromptStyle.Fullscreen };
        LabeledComboRaw("Stil prompta", styleValues.Select(s => s.Label()).ToArray(),
            Array.IndexOf(styleValues, _engine.Settings.PromptStyle),
            i => _engine.MutateSettings(s => s.PromptStyle = styleValues[i]));

        Toggle("Zvuk kod prompta", _engine.Settings.SoundEnabled,
            v => _engine.MutateSettings(s => s.SoundEnabled = v));
        Gap(8);
    }

    private void BuildIdleSection()
    {
        SectionHeader("ODSUTNOST");
        Toggle("Detekcija odsutnosti", _engine.Settings.IdleDetectionEnabled, v =>
        {
            _engine.MutateSettings(s => s.IdleDetectionEnabled = v);
            _idleThreshold.Enabled = v;
        });
        _idleThreshold = LabeledCombo("Prag neaktivnosti", new[] { 3, 5, 10, 15 }, _engine.Settings.IdleThresholdMinutes, " min",
            v => _engine.MutateSettings(s => s.IdleThresholdMinutes = v));
        _idleThreshold.Enabled = _engine.Settings.IdleDetectionEnabled;
        Caption("Ako je računalo zaključano ili nema aktivnosti dulje od praga, prompt se odgađa dok se ne vratiš, a odsutnost se bilježi kao pauza.");
        Gap(8);
    }

    private void BuildHistorySection()
    {
        SectionHeader("POVIJEST");
        LabeledCombo("Broj zapamćenih unosa", new[] { 5, 10, 15, 25, 50 }, _engine.Settings.HistoryLimit, "",
            v => _engine.MutateSettings(s => s.HistoryLimit = v));
        Gap(8);
    }

    private void BuildSystemSection()
    {
        SectionHeader("SUSTAV");
        Toggle("Pokreni kod prijave (launch at login)", _engine.Settings.LaunchAtLogin, v =>
        {
            _engine.MutateSettings(s => s.LaunchAtLogin = v);
            _launchStatus.Text = _engine.LaunchAtLoginStatus ?? "";
            _launchStatus.Visible = !string.IsNullOrEmpty(_launchStatus.Text);
        });
        _launchStatus = new Label { AutoSize = false, Text = _engine.LaunchAtLoginStatus ?? "", ForeColor = Palette.Orange, BackColor = Palette.Black, Font = Brand.Ui(8f), Location = new Point(20, _y), Width = ClientSize.Width - 40 };
        _launchStatus.Height = MeasureWrap(_launchStatus.Text, _launchStatus.Font, _launchStatus.Width);
        _launchStatus.Visible = !string.IsNullOrEmpty(_launchStatus.Text);
        _stack.Controls.Add(_launchStatus);
        _y += Math.Max(_launchStatus.Visible ? _launchStatus.Height : 0, 0);

        Toggle("Podsjetnik kod pokretanja (pop-up)", _engine.Settings.ShowStartupReminder,
            v => _engine.MutateSettings(s => s.ShowStartupReminder = v));
        Caption("Kad se app pokrene, iskoči pop-up da te podsjeti da pokreneš radni dan ako tracking još nije aktivan.");
        Gap(8);
    }

    private void BuildDataSection()
    {
        SectionHeader("PODACI");
        var label = new Label { AutoSize = true, Text = "Lokacija", ForeColor = Palette.Gray, BackColor = Palette.Black, Location = new Point(20, _y) };
        _stack.Controls.Add(label);
        _y += label.Height + 2;

        var path = new TextBox { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Palette.White.OverBlack(0.06), ForeColor = Palette.Gray, Font = Brand.Mono(8f), Text = Store.Directory, Location = new Point(20, _y), Width = ClientSize.Width - 40, Multiline = true, Height = 34 };
        _stack.Controls.Add(path);
        _y += path.Height + 8;

        var open = new FlatButton { Text = "Otvori folder s podacima", TextColor = Palette.Yellow, Fill = Palette.Yellow.With(0.12), BorderColor = Palette.Yellow.With(0.5), BorderWidth = 1, CornerRadius = 6, Font = Brand.Ui(9f, FontStyle.Bold), BackColor = Palette.Black, Size = new Size(190, 28), Location = new Point(20, _y) };
        open.Click += (_, _) => { try { System.Diagnostics.Process.Start("explorer.exe", Store.Directory); } catch { } };
        _stack.Controls.Add(open);
        _y += open.Height + 16;
    }

    // MARK: - Building blocks

    private void SectionHeader(string text)
    {
        var label = new TrackedLabel { Text = text, Font = Brand.Ui(8.5f, FontStyle.Bold), ForeColor = Palette.Yellow, Tracking = 1.5f, BackColor = Palette.Black, Location = new Point(20, _y) };
        _stack.Controls.Add(label);
        _y += label.Height + 8;
    }

    private ComboBox LabeledCombo(string label, int[] values, int current, string suffix, Action<int> onChange)
    {
        var combo = LabeledComboRaw(label, values.Select(v => $"{v}{suffix}").ToArray(), Math.Max(0, Array.IndexOf(values, current)),
            i => onChange(values[i]));
        return combo;
    }

    private ComboBox LabeledComboRaw(string label, string[] items, int selectedIndex, Action<int> onChange)
    {
        var lbl = new Label { AutoSize = false, Text = label, ForeColor = Palette.White, BackColor = Palette.Black, Location = new Point(20, _y + 4), Size = new Size(210, 22), TextAlign = ContentAlignment.MiddleLeft };
        _stack.Controls.Add(lbl);

        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Palette.White.OverBlack(0.1),
            ForeColor = Palette.White,
            Font = Brand.Ui(9.5f),
            DrawMode = DrawMode.OwnerDrawFixed,
            Location = new Point(236, _y),
            Width = ClientSize.Width - 20 - 236,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        combo.Items.AddRange(items.Cast<object>().ToArray());
        combo.DrawItem += DarkComboDrawItem;
        if (selectedIndex >= 0 && selectedIndex < items.Length) combo.SelectedIndex = selectedIndex;
        combo.SelectedIndexChanged += (_, _) => { if (combo.SelectedIndex >= 0) onChange(combo.SelectedIndex); };
        _stack.Controls.Add(combo);

        _y += 34;
        return combo;
    }

    private static void DarkComboDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo || e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using var back = new SolidBrush(selected ? Palette.Yellow.With(0.25) : Palette.White.OverBlack(0.1));
        e.Graphics.FillRectangle(back, e.Bounds);
        TextRenderer.DrawText(e.Graphics, combo.Items[e.Index]?.ToString() ?? "", combo.Font, e.Bounds, Palette.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    private void Toggle(string text, bool value, Action<bool> onChange)
    {
        var box = new CheckBox
        {
            Text = text,
            Checked = value,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Palette.White,
            BackColor = Palette.Black,
            Font = Brand.Ui(9.5f),
            Location = new Point(20, _y),
        };
        box.FlatAppearance.CheckedBackColor = Palette.Yellow;
        box.CheckedChanged += (_, _) => onChange(box.Checked);
        _stack.Controls.Add(box);
        _y += box.Height + 8;
    }

    private void Caption(string text)
    {
        var label = new Label { AutoSize = false, Text = text, ForeColor = Palette.Gray.With(0.7), BackColor = Palette.Black, Font = Brand.Ui(8f), Location = new Point(20, _y), Width = ClientSize.Width - 40 };
        label.Height = MeasureWrap(text, label.Font, label.Width);
        _stack.Controls.Add(label);
        _y += label.Height + 4;
    }

    private void Gap(int px) => _y += px;

    private static int MeasureWrap(string text, Font font, int width)
        => TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 2;
}
