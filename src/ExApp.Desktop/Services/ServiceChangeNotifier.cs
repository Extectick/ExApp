namespace ExApp.Desktop.Services;

internal static class ServiceChangeNotifier
{
    public static event EventHandler? Changed;

    public static void NotifyChanged() => Changed?.Invoke(null, EventArgs.Empty);
}
