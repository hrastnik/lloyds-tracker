using System.Runtime.InteropServices;
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

        // System-DPI aware: render at the primary monitor's scale factor so the UI stays
        // crisp on high-DPI displays. The forms lay out in fixed 96-DPI pixels and scale
        // every dimension and font from Brand.Dpi, captured here before any window is
        // created (SetHighDpiMode must run first). See app.manifest.
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Brand.Dpi = Math.Max(96, (int)GetDpiForSystem());

        // EnableVisualStyles + text-rendering defaults.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // No main window / no taskbar entry — a tray-only app (like macOS LSUIElement).
        Application.Run(new TrayApplicationContext());

        GC.KeepAlive(mutex);
    }

    // System DPI (96 = 100%). Available on Windows 10 1607+, which the manifest targets.
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
