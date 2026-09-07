using System.Drawing;
using System.Windows.Forms;

namespace LloydsTracker;

/// <summary>Ispravak postojećeg unosa iz kronološkog pregleda — opis, vrijeme i vrsta
/// (rad/pauza). Spojeni red (`2×`) se ispravlja kao jedna cjelina: promjena samo
/// opisa/vrste zadržava blokove, a promjena vremena ih stopi u jedan unos (novi raspon
/// nema stare granice). Zrcali macOS EntryEditView.</summary>
internal sealed class EntryEditForm : Form
{
    private readonly ChronoRow _row;
    private readonly IReadOnlyList<string> _history;

    private readonly PromptTextField _text;
    private readonly ComboBox _startHour;
    private readonly ComboBox _startMinute;
    private readonly ComboBox _endHour;
    private readonly ComboBox _endMinute;
    private readonly Label _duration;
    private readonly Label _warning;
    private readonly FlatButton _work;
    private readonly FlatButton _pause;
    private readonly FlatButton _save;

    private EntryKind _kind;
    private int _y;

    /// <summary>Rezultat — čita se nakon <c>DialogResult.OK</c>.</summary>
    public string EntryText => _text.Value.Trim();
    public DateTime EntryStart => Combine(_row.Start, _startHour, _startMinute);
    public DateTime EntryEnd => Combine(_row.End, _endHour, _endMinute);
    public EntryKind Kind => _kind;

    private const int Pad = 22;
    private const int FormWidth = 420;

    public EntryEditForm(ChronoRow row, IReadOnlyList<string> history)
    {
        _row = row;
        _history = history;
        _kind = row.Kind;

        Text = "Ispravi unos";
        Icon = TrayIconFactory.Window;
        ClientSize = new Size(Brand.S(FormWidth), Brand.S(320));
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Palette.Black;
        Font = Brand.Ui(9.5f);
        KeyPreview = true;

        int inner = Brand.S(FormWidth) - Brand.S(Pad) * 2;
        _y = Brand.S(Pad);

        BuildHeader(inner);

        _y += Brand.S(16);
        SectionLabel("OPIS");
        _text = new PromptTextField(row.Start, big: false, Palette.White.OverBlack(0.07), "npr. Projekt X — opis zadatka")
        {
            Location = new Point(Brand.S(Pad), _y),
            Width = inner,
        };
        _text.Value = row.Text;
        _text.ValueChanged += Sync;
        _text.SubmitRequested += Save;
        _text.EscapeRequested += Cancel;
        Controls.Add(_text);
        _y += _text.Height + Brand.S(6);

        if (_history.Count > 0) BuildHistoryPicker(inner);

        _y += Brand.S(10);
        int col = Brand.S(120);
        SectionLabelAt("OD", Brand.S(Pad));
        SectionLabelAt("DO", Brand.S(Pad) + col);
        var durationLabel = SectionLabelAt("TRAJANJE", Brand.S(Pad) + col * 2);
        _y += durationLabel.Height + Brand.S(6);

        (_startHour, _startMinute) = TimeCombos(row.Start, Brand.S(Pad));
        (_endHour, _endMinute) = TimeCombos(row.End, Brand.S(Pad) + col);
        _duration = new Label
        {
            AutoSize = false,
            Font = Brand.Mono(9.5f, FontStyle.Bold),
            ForeColor = Palette.Yellow,
            BackColor = Palette.Black,
            Location = new Point(Brand.S(Pad) + col * 2, _y + Brand.S(3)),
            Size = new Size(Brand.S(80), Brand.S(20)),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Controls.Add(_duration);
        _y += _startHour.Height + Brand.S(6);

        _y += Brand.S(14);
        SectionLabel("VRSTA");
        (_work, _pause) = BuildKindPills();
        _y += Brand.S(28) + Brand.S(12);

        _warning = new Label
        {
            AutoSize = false,
            Font = Brand.Ui(8.5f),
            BackColor = Palette.Black,
            Location = new Point(Brand.S(Pad), _y),
            Size = new Size(inner, Brand.S(18)),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Controls.Add(_warning);
        _y += Brand.S(18) + Brand.S(10);

        _save = BuildFooter(inner);
        _y += Brand.S(30) + Brand.S(Pad);
        ClientSize = new Size(Brand.S(FormWidth), _y);

        Sync();
    }

    // MARK: - Building blocks

    private void BuildHeader(int inner)
    {
        var square = new Panel
        {
            BackColor = Palette.Yellow,
            Size = new Size(Brand.S(16), Brand.S(16)),
            Location = new Point(Brand.S(Pad), _y),
        };
        Controls.Add(square);

        var title = new TrackedLabel
        {
            Text = "ISPRAVI UNOS",
            Font = Brand.Ui(10f, FontStyle.Bold),
            ForeColor = Palette.White,
            Tracking = 1.5f,
            BackColor = Palette.Black,
        };
        title.Location = new Point(square.Right + Brand.S(8), _y + (Brand.S(16) - title.Height) / 2);
        Controls.Add(title);

        var day = new Label
        {
            AutoSize = false,
            Text = Fmt.DayTitle(_row.Start),
            Font = Brand.Ui(8.5f),
            ForeColor = Palette.Gray.With(0.7),
            BackColor = Palette.Black,
            Size = new Size(Brand.S(180), Brand.S(16)),
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(Brand.S(Pad) + inner - Brand.S(180), _y),
        };
        Controls.Add(day);
        _y += Brand.S(16);
    }

    /// <summary>"Iz povijesti…" — macOS ovdje ima Menu; WinForms nema borderless menu gumb,
    /// pa je to drop-down koji se nakon odabira vrati na natpis.</summary>
    private void BuildHistoryPicker(int inner)
    {
        var items = new List<string> { "Iz povijesti…" };
        items.AddRange(_history);
        var combo = Dark.Combo(items.ToArray(), 0, new Point(Brand.S(Pad), _y), Math.Min(inner, Brand.S(200)));
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (combo.SelectedIndex <= 0) return;
            _text.Value = items[combo.SelectedIndex];
            combo.SelectedIndex = 0;
            Sync();
        };
        Controls.Add(combo);
        _y += combo.Height + Brand.S(4);
    }

    private (ComboBox Hour, ComboBox Minute) TimeCombos(DateTime value, int x)
    {
        var hours = Enumerable.Range(0, 24).Select(h => $"{h:D2}").ToArray();
        var minutes = Enumerable.Range(0, 60).Select(m => $"{m:D2}").ToArray();
        int w = Brand.S(50);

        var hour = Dark.Combo(hours, value.Hour, new Point(x, _y), w);
        var minute = Dark.Combo(minutes, value.Minute, new Point(x + w + Brand.S(4), _y), w);
        hour.SelectedIndexChanged += (_, _) => Sync();
        minute.SelectedIndexChanged += (_, _) => Sync();
        Controls.Add(hour);
        Controls.Add(minute);
        return (hour, minute);
    }

    private (FlatButton Work, FlatButton Pause) BuildKindPills()
    {
        FlatButton Pill(string title, EntryKind kind, int x)
        {
            var font = Brand.Ui(9.5f, FontStyle.Bold);
            var pill = new FlatButton
            {
                Text = title,
                CornerRadius = 8,
                Font = font,
                BackColor = Palette.Black,
                Size = new Size(Brand.S(84), Brand.S(28)),
                Location = new Point(x, _y),
            };
            pill.Click += (_, _) => { _kind = kind; Sync(); };
            Controls.Add(pill);
            return pill;
        }

        var work = Pill("Rad", EntryKind.Work, Brand.S(Pad));
        var pause = Pill("Pauza", EntryKind.Pause, Brand.S(Pad) + Brand.S(84) + Brand.S(6));
        return (work, pause);
    }

    private FlatButton BuildFooter(int inner)
    {
        var save = new FlatButton
        {
            Text = "Spremi",
            TextColor = Palette.Black,
            Fill = Palette.Yellow,
            CornerRadius = 10,
            Font = Brand.Ui(10f, FontStyle.Bold),
            BackColor = Palette.Black,
            Size = new Size(Brand.S(88), Brand.S(30)),
        };
        save.Location = new Point(Brand.S(Pad) + inner - save.Width, _y);
        save.Click += (_, _) => Save();
        Controls.Add(save);

        var cancel = new FlatButton
        {
            Text = "Odustani",
            TextColor = Palette.Gray,
            Fill = Color.Transparent,
            BorderColor = Palette.White.OverBlack(0.2),
            BorderWidth = 1,
            CornerRadius = 10,
            Font = Brand.Ui(10f, FontStyle.Bold),
            BackColor = Palette.Black,
            Size = new Size(Brand.S(88), Brand.S(30)),
        };
        cancel.Location = new Point(save.Left - Brand.S(10) - cancel.Width, _y);
        cancel.Click += (_, _) => Cancel();
        Controls.Add(cancel);

        return save;
    }

    private void SectionLabel(string title)
        => _y += SectionLabelAt(title, Brand.S(Pad)).Height + Brand.S(6);

    private TrackedLabel SectionLabelAt(string title, int x)
    {
        var label = new TrackedLabel
        {
            Text = title,
            Font = Brand.Ui(7.5f, FontStyle.Bold),
            ForeColor = Palette.Gray.With(0.6),
            Tracking = 1.2f,
            BackColor = Palette.Black,
            Location = new Point(x, _y),
        };
        Controls.Add(label);
        return label;
    }

    // MARK: - State

    private static DateTime Combine(DateTime day, ComboBox hour, ComboBox minute)
        => day.Date.AddHours(Math.Max(0, hour.SelectedIndex)).AddMinutes(Math.Max(0, minute.SelectedIndex));

    private bool TimesChanged
        => Math.Abs((EntryStart - _row.Start).TotalSeconds) > 1
            || Math.Abs((EntryEnd - _row.End).TotalSeconds) > 1;

    private bool IsValid => EntryText.Length > 0 && EntryEnd > EntryStart;

    /// <summary>Trajanje, upozorenja i stanje gumba nakon svake promjene — trajanje nakon
    /// ispravka je odmah vidljivo.</summary>
    private void Sync()
    {
        bool ok = EntryEnd > EntryStart;
        _duration.Text = Fmt.Dur(Math.Max(0, (EntryEnd - EntryStart).TotalSeconds));
        _duration.ForeColor = ok ? Palette.Yellow : Color.Orange;

        if (!ok)
        {
            _warning.Text = "Kraj mora biti nakon početka.";
            _warning.ForeColor = Color.Orange;
        }
        else if (_row.IsMerged && TimesChanged)
        {
            _warning.Text = $"Promjena vremena stapa {_row.Ids.Count} bloka u jedan unos.";
            _warning.ForeColor = Palette.Yellow.With(0.8);
        }
        else
        {
            _warning.Text = "";
        }

        bool work = _kind == EntryKind.Work;
        StylePill(_work, work);
        StylePill(_pause, !work);

        _save.Fill = Palette.Yellow.With(IsValid ? 1 : 0.35);
        _save.Invalidate();
    }

    private static void StylePill(FlatButton pill, bool active)
    {
        pill.Fill = active ? Palette.Yellow : Color.Transparent;
        pill.TextColor = active ? Palette.Black : Palette.Gray;
        pill.BorderColor = active ? Color.Transparent : Palette.White.OverBlack(0.2);
        pill.BorderWidth = active ? 0 : 1;
        pill.Invalidate();
    }

    private void Save()
    {
        if (!IsValid) return;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Cancel()
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Cancel();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _text.FocusBox();
    }
}
