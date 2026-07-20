using System.Windows.Forms;

namespace LloydsTracker;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single instance — a second launch just exits (the tray icon is already there).
        using var mutex = new Mutex(initiallyOwned: true, "LloydsTracker.SingleInstance.Mutex", out bool isNew);
        if (!isNew) return;

        // EnableVisualStyles + text-rendering defaults. DPI awareness is declared in app.manifest.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // No main window / no taskbar entry — a tray-only app (like macOS LSUIElement).
        Application.Run(new TrayApplicationContext());

        GC.KeepAlive(mutex);
    }
}
