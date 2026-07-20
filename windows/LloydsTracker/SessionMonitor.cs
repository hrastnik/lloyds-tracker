using Microsoft.Win32;

namespace LloydsTracker;

/// <summary>Tracks whether the workstation is locked, via SystemEvents.SessionSwitch.
/// Windows equivalent of the macOS screenIsLocked / screenIsUnlocked notifications.</summary>
internal sealed class SessionMonitor : IDisposable
{
    public bool IsLocked { get; private set; }
    public DateTime? LockedAt { get; private set; }

    /// <summary>Raised (on the UI thread) whenever the lock state changes.</summary>
    public event Action? Changed;

    public SessionMonitor()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                IsLocked = true;
                LockedAt = DateTime.Now;
                Changed?.Invoke();
                break;

            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                IsLocked = false;
                Changed?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
