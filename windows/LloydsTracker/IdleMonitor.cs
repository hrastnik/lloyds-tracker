using System.Runtime.InteropServices;

namespace LloydsTracker;

/// <summary>Seconds since the last user input (keyboard, mouse, scroll) system-wide.
/// Windows equivalent of macOS CGEventSource.secondsSinceLastEventType.</summary>
internal static class IdleMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;

        // Both values are 32-bit millisecond tick counts that wrap ~every 49.7 days;
        // unsigned subtraction handles the wrap correctly.
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return idleMs / 1000.0;
    }
}
