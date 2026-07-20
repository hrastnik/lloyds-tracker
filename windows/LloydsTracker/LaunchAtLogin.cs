using Microsoft.Win32;

namespace LloydsTracker;

/// <summary>Launch-at-login via the per-user Run registry key — the Windows
/// counterpart to macOS SMAppService. No admin rights required.</summary>
internal static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LloydsTracker";

    /// <summary>Path to the running executable (the single-file host on a published build).</summary>
    private static string ExecutablePath
        => Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var value = key?.GetValue(ValueName) as string;
        return !string.IsNullOrEmpty(value);
    }

    /// <summary>Applies the desired state. Returns null on success, or a Croatian
    /// error message for the settings UI (mirrors launchAtLoginStatus).</summary>
    public static string? Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return "Greška: ne mogu otvoriti registry ključ za pokretanje.";

            if (enabled)
            {
                string exe = ExecutablePath;
                if (string.IsNullOrEmpty(exe))
                    return "Greška: ne mogu odrediti putanju aplikacije.";
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"Greška: {ex.Message}";
        }
    }
}
