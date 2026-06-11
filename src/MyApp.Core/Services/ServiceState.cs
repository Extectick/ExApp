namespace MyApp.Core.Services;

public enum ServiceState
{
    NotInstalled,
    Installed,
    Starting,
    Running,
    Stopping,
    Stopped,
    Crashed,
    Error,
    NeedsPermission,
    NeedsRestart,
    UpdateAvailable,
    UpdateFailed
}
