using System.Windows.Forms;

namespace LloydsTracker;

/// <summary>Owns the tray icon, the engine, and the app's windows — the WinForms
/// equivalent of the SwiftUI App scene (MenuBarExtra + Window + Settings).</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly TrackerEngine _engine;
    private readonly NotifyIcon _tray;
    private readonly MenuBarPopover _popover;
    private SummaryForm? _summary;
    private SettingsForm? _settings;

    private long _lastHidden;

    public TrayApplicationContext()
    {
        _engine = new TrackerEngine();

        _tray = new NotifyIcon
        {
            Icon = TrayIconFactory.For(_engine.State),
            Text = "Lloyds Tracker",
            Visible = true,
        };
        _tray.MouseClick += OnTrayClick;
        _tray.ContextMenuStrip = BuildTrayMenu();

        _popover = new MenuBarPopover(_engine, OpenSummary, OpenSettings, Quit);
        _popover.VisibleChanged += (_, _) => { if (!_popover.Visible) _lastHidden = Environment.TickCount64; };

        _engine.OpenSummary = OpenSummary;
        _engine.Changed += UpdateTray;

        UpdateTray();
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip { RenderMode = ToolStripRenderMode.System };
        menu.Items.Add("Pregled dana", null, (_, _) => OpenSummary());
        menu.Items.Add("Postavke…", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Izlaz", null, (_, _) => Quit());
        return menu;
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        // If the popover was just hidden by the click's deactivation, treat this as a close.
        if (Environment.TickCount64 - _lastHidden < 250) return;
        _popover.Toggle();
    }

    private void UpdateTray()
    {
        _tray.Icon = TrayIconFactory.For(_engine.State);
        _tray.Text = $"Lloyds Tracker — {_engine.StatusText}";
    }

    private void OpenSummary()
    {
        if (_summary is { IsDisposed: false })
        {
            _summary.RefreshData();
            _summary.WindowState = FormWindowState.Normal;
            _summary.Activate();
            return;
        }
        _summary = new SummaryForm(_engine);
        _summary.FormClosed += (_, _) => _summary = null;
        _summary.Show();
        _summary.Activate();
    }

    private void OpenSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.WindowState = FormWindowState.Normal;
            _settings.Activate();
            return;
        }
        _settings = new SettingsForm(_engine);
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    private void Quit()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _engine.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _popover.Dispose();
            _engine.Dispose();
        }
        base.Dispose(disposing);
    }
}
