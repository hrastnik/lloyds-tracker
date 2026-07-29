using System.Drawing;
using System.Windows.Forms;

namespace LloydsTracker;

/// <summary>Settings window — mirrors the SwiftUI SettingsView: the same three tabs
/// (Promptanje / Radni dan / Sustav) with the same sections. One long list outgrew the
/// screen height (worse here, since every dimension is DPI-scaled), so it's split into
/// short pages; the window is sized to the tallest page and each page scrolls if needed.</summary>
internal sealed class SettingsForm : Form
{
    private readonly TrackerEngine _engine;
    /// <summary>The page the Build*Section helpers currently append to.</summary>
    private Panel _stack = null!;
    private int _y;

    private ComboBox _idleThreshold = null!;
    private ComboBox _autoStopHour = null!;
    private ComboBox _autoStopMinute = null!;
    private Label _launchStatus = null!;

    private readonly List<FlatButton> _pills = new();
    private readonly List<Panel> _pages = new();

    public SettingsForm(TrackerEngine engine)
    {
        _engine = engine;
        Text = "Postavke";
        // Brand tile in the title bar / taskbar / Alt+Tab, like the macOS AppIcon.
        Icon = TrayIconFactory.Window;
        // Width is final from the start (control widths derive from it); the height is set
        // below, once the pages have been measured.
        ClientSize = new Size(Brand.S(460), Brand.S(200));
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Palette.Black;
        Font = Brand.Ui(9.5f);

        int tabsHeight = Brand.S(46);
        var tabBar = new Panel { Location = new Point(0, 0), Size = new Size(ClientSize.Width, tabsHeight), BackColor = Palette.Black };
        Controls.Add(tabBar);

        int contentHeight = 0;
        AddPage(tabBar, tabsHeight, "Promptanje", () => { BuildPromptSection(); BuildHistorySection(); }, ref contentHeight);
        AddPage(tabBar, tabsHeight, "Radni dan", () => { BuildAutoStopSection(); BuildIdleSection(); }, ref contentHeight);
        AddPage(tabBar, tabsHeight, "Sustav", () => { BuildSystemSection(); BuildDataSection(); }, ref contentHeight);

        ClientSize = new Size(ClientSize.Width, tabsHeight + contentHeight);
        foreach (var page in _pages) page.Size = new Size(ClientSize.Width, contentHeight);
        SelectTab(0);
    }

    /// <summary>Build one tab: a pill in the tab bar plus its page. Grows
    /// <paramref name="contentHeight"/> to fit the tallest page.</summary>
    private void AddPage(Panel tabBar, int tabsHeight, string title, Action build, ref int contentHeight)
    {
        var page = new Panel
        {
            Location = new Point(0, tabsHeight),
            Size = new Size(ClientSize.Width, Brand.S(100)),
            BackColor = Palette.Black,
            AutoScroll = true,
            Visible = false,
        };
        _stack = page;
        _y = Brand.S(16);
        build();
        contentHeight = Math.Max(contentHeight, _y + Brand.S(8));
        Controls.Add(page);
        _pages.Add(page);

        var font = Brand.Ui(9.5f, FontStyle.Bold);
        int x = _pills.Count == 0 ? Brand.S(16) : _pills[^1].Right + Brand.S(6);
        var pill = new FlatButton
        {
            Text = title,
            CornerRadius = 8,
            Font = font,
            BackColor = Palette.Black,
            Size = new Size(TextRenderer.MeasureText(title, font).Width + Brand.S(24), Brand.S(28)),
            Location = new Point(x, (tabsHeight - Brand.S(28)) / 2),
        };
        int index = _pills.Count;
        pill.Click += (_, _) => SelectTab(index);
        _pills.Add(pill);
        tabBar.Controls.Add(pill);
    }

    private void SelectTab(int index)
    {
        for (int i = 0; i < _pages.Count; i++)
        {
            bool active = i == index;
            _pages[i].Visible = active;
            _pills[i].Fill = active ? Palette.Yellow : Color.Transparent;
            _pills[i].TextColor = active ? Palette.Black : Palette.Gray;
            _pills[i].BorderColor = active ? Color.Transparent : Palette.White.OverBlack(0.2);
            _pills[i].BorderWidth = active ? 0 : 1;
            _pills[i].Invalidate();
        }
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

    private void BuildAutoStopSection()
    {
        SectionHeader("AUTOMATSKO ZAUSTAVLJANJE");
        Toggle("Zaustavi tracking u zadano vrijeme", _engine.Settings.AutoStopEnabled, v =>
        {
            _engine.MutateSettings(s => s.AutoStopEnabled = v);
            _autoStopHour.Enabled = v;
            _autoStopMinute.Enabled = v;
        });

        (_autoStopHour, _autoStopMinute) = LabeledTimeCombos(
            "Vrijeme",
            _engine.Settings.AutoStopHour,
            _engine.Settings.AutoStopMinute,
            (h, m) => _engine.MutateSettings(s => { s.AutoStopHour = h; s.AutoStopMinute = m; }));
        _autoStopHour.Enabled = _engine.Settings.AutoStopEnabled;
        _autoStopMinute.Enabled = _engine.Settings.AutoStopEnabled;

        Caption("Minutu prije iskoči upozorenje s produženjem (+15 / +30 / +45 / +1 h), koje vrijedi samo za taj dan. Bez reakcije dan se sam zatvara — pa tracking ne ostane pokrenut preko noći.");
        Gap(8);
    }

    private void BuildIdleSection()
    {
        SectionHeader("ODSUTNOST");
        Toggle("Detekcija neaktivnosti (tipkovnica/miš)", _engine.Settings.IdleDetectionEnabled, v =>
        {
            _engine.MutateSettings(s => s.IdleDetectionEnabled = v);
            _idleThreshold.Enabled = v;
        });
        _idleThreshold = LabeledCombo("Prag neaktivnosti", new[] { 3, 5, 10, 15 }, _engine.Settings.IdleThresholdMinutes, " min",
            v => _engine.MutateSettings(s => s.IdleThresholdMinutes = v));
        _idleThreshold.Enabled = _engine.Settings.IdleDetectionEnabled;
        Toggle("Bilježi pauzu kad je ekran zaključan", _engine.Settings.LockPauseEnabled,
            v => _engine.MutateSettings(s => s.LockPauseEnabled = v));
        Caption("Uključeno: razdoblje odsutnosti se bilježi kao pauza, a prompt čeka da se vratiš. Isključeno: prompt te u zakazano vrijeme samo pita što si radio.");
        Gap(8);
    }

    private void BuildHistorySection()
    {
        SectionHeader("POVIJEST");
        LabeledCombo("Broj zapamćenih unosa", new[] { 5, 10, 15, 25, 50 }, _engine.Settings.HistoryLimit, "",
            v => _engine.MutateSettings(s => s.HistoryLimit = v));
        Caption("Koliko se nedavnih unosa pamti za pre-fill i listanje (↑/↓) u promptu.");
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
        _launchStatus = new Label { AutoSize = false, Text = _engine.LaunchAtLoginStatus ?? "", ForeColor = Palette.Orange, BackColor = Palette.Black, Font = Brand.Ui(8f), Location = new Point(Brand.S(20), _y), Width = ClientSize.Width - Brand.S(40) };
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
        var label = new Label { AutoSize = true, Text = "Lokacija", ForeColor = Palette.Gray, BackColor = Palette.Black, Location = new Point(Brand.S(20), _y) };
        _stack.Controls.Add(label);
        _y += label.Height + Brand.S(2);

        var path = new TextBox { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Palette.White.OverBlack(0.06), ForeColor = Palette.Gray, Font = Brand.Mono(8f), Text = Store.Directory, Location = new Point(Brand.S(20), _y), Width = ClientSize.Width - Brand.S(40), Multiline = true, Height = Brand.S(34) };
        _stack.Controls.Add(path);
        _y += path.Height + Brand.S(8);

        var open = new FlatButton { Text = "Otvori folder s podacima", TextColor = Palette.Yellow, Fill = Palette.Yellow.With(0.12), BorderColor = Palette.Yellow.With(0.5), BorderWidth = 1, CornerRadius = 6, Font = Brand.Ui(9f, FontStyle.Bold), BackColor = Palette.Black, Size = new Size(Brand.S(190), Brand.S(28)), Location = new Point(Brand.S(20), _y) };
        open.Click += (_, _) => { try { System.Diagnostics.Process.Start("explorer.exe", Store.Directory); } catch { } };
        _stack.Controls.Add(open);
        _y += open.Height + Brand.S(16);
    }

    // MARK: - Building blocks

    private void SectionHeader(string text)
    {
        var label = new TrackedLabel { Text = text, Font = Brand.Ui(8.5f, FontStyle.Bold), ForeColor = Palette.Yellow, Tracking = 1.5f, BackColor = Palette.Black, Location = new Point(Brand.S(20), _y) };
        _stack.Controls.Add(label);
        _y += label.Height + Brand.S(8);
    }

    private ComboBox LabeledCombo(string label, int[] values, int current, string suffix, Action<int> onChange)
    {
        var combo = LabeledComboRaw(label, values.Select(v => $"{v}{suffix}").ToArray(), Math.Max(0, Array.IndexOf(values, current)),
            i => onChange(values[i]));
        return combo;
    }

    private ComboBox LabeledComboRaw(string label, string[] items, int selectedIndex, Action<int> onChange)
    {
        RowLabel(label);

        var combo = DarkCombo(items, selectedIndex, new Point(Brand.S(236), _y),
            ClientSize.Width - Brand.S(20) - Brand.S(236),
            AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
        combo.SelectedIndexChanged += (_, _) => { if (combo.SelectedIndex >= 0) onChange(combo.SelectedIndex); };
        _stack.Controls.Add(combo);

        _y += Brand.S(34);
        return combo;
    }

    /// <summary>Hour + minute pickers on one row (the auto-stop time). Minutes step by 5,
    /// but a value written by the macOS build with any minute stays selectable.</summary>
    private (ComboBox Hour, ComboBox Minute) LabeledTimeCombos(string label, int hour, int minute, Action<int, int> onChange)
    {
        RowLabel(label);

        int left = Brand.S(236);
        int gap = Brand.S(8);
        int comboW = (ClientSize.Width - Brand.S(20) - left - gap) / 2;

        var hours = Enumerable.Range(0, 24).ToList();
        var minutes = Enumerable.Range(0, 12).Select(i => i * 5).ToList();
        minute = Math.Clamp(minute, 0, 59);
        if (!minutes.Contains(minute)) { minutes.Add(minute); minutes.Sort(); }

        var hourCombo = DarkCombo(hours.Select(h => h.ToString("D2")).ToArray(),
            hours.IndexOf(Math.Clamp(hour, 0, 23)), new Point(left, _y), comboW, AnchorStyles.Top | AnchorStyles.Left);
        var minuteCombo = DarkCombo(minutes.Select(m => m.ToString("D2")).ToArray(),
            minutes.IndexOf(minute), new Point(left + comboW + gap, _y), comboW, AnchorStyles.Top | AnchorStyles.Left);

        void Changed()
        {
            if (hourCombo.SelectedIndex < 0 || minuteCombo.SelectedIndex < 0) return;
            onChange(hours[hourCombo.SelectedIndex], minutes[minuteCombo.SelectedIndex]);
        }
        hourCombo.SelectedIndexChanged += (_, _) => Changed();
        minuteCombo.SelectedIndexChanged += (_, _) => Changed();

        _stack.Controls.Add(hourCombo);
        _stack.Controls.Add(minuteCombo);
        _y += Brand.S(34);
        return (hourCombo, minuteCombo);
    }

    private void RowLabel(string label)
    {
        var lbl = new Label { AutoSize = false, Text = label, ForeColor = Palette.White, BackColor = Palette.Black, Location = new Point(Brand.S(20), _y + Brand.S(4)), Size = new Size(Brand.S(210), Brand.S(22)), TextAlign = ContentAlignment.MiddleLeft };
        _stack.Controls.Add(lbl);
    }

    private static ComboBox DarkCombo(string[] items, int selectedIndex, Point location, int width, AnchorStyles anchor)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Palette.White.OverBlack(0.1),
            ForeColor = Palette.White,
            Font = Brand.Ui(9.5f),
            DrawMode = DrawMode.OwnerDrawFixed,
            Location = location,
            Width = width,
            Anchor = anchor,
        };
        combo.Items.AddRange(items.Cast<object>().ToArray());
        combo.DrawItem += DarkComboDrawItem;
        if (selectedIndex >= 0 && selectedIndex < items.Length) combo.SelectedIndex = selectedIndex;
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
            Location = new Point(Brand.S(20), _y),
        };
        box.FlatAppearance.CheckedBackColor = Palette.Yellow;
        box.CheckedChanged += (_, _) => onChange(box.Checked);
        _stack.Controls.Add(box);
        _y += box.Height + Brand.S(8);
    }

    private void Caption(string text)
    {
        var label = new Label { AutoSize = false, Text = text, ForeColor = Palette.Gray.With(0.7), BackColor = Palette.Black, Font = Brand.Ui(8f), Location = new Point(Brand.S(20), _y), Width = ClientSize.Width - Brand.S(40) };
        label.Height = MeasureWrap(text, label.Font, label.Width);
        _stack.Controls.Add(label);
        _y += label.Height + Brand.S(4);
    }

    private void Gap(int px) => _y += Brand.S(px);

    private static int MeasureWrap(string text, Font font, int width)
        => TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + Brand.S(2);
}
