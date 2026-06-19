using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using ExApp.Desktop.Services;

namespace ExApp.Desktop;

public partial class App : Application
{
    private readonly AgentProcessManager _agentProcessManager = new();
    private MainWindow? _window;

    public MainWindow? MainWindow => _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLogger.Info("Application launch requested.");
        LocalizationService.Current.Initialize();

        var appInstance = AppInstance.FindOrRegisterForKey("ExApp.Desktop");
        if (!appInstance.IsCurrent)
        {
            AppLogger.Info("Redirecting launch to existing instance.");
            await appInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Environment.Exit(0);
            return;
        }

        appInstance.Activated += OnAppInstanceActivated;

        try
        {
            if (await ApplicationUpdateService.Current.TryRecoverInterruptedUpdateAsync())
            {
                AppLogger.Info("Interrupted update recovery launched.");
                Environment.Exit(0);
                return;
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Interrupted update recovery failed to launch", exception);
        }

        try
        {
            await _agentProcessManager.EnsureRunningAsync();
            AppLogger.Info("Agent connection established.");
        }
        catch (Exception exception)
        {
            AppLogger.Error("Agent startup failed", exception);
        }

        _window = new MainWindow();
        ApplyTheme(AppSettings.ThemePreference);
        _window.Activate();
        AppLogger.Info("Main window activated.");

        if (AppSettings.AutomaticUpdateChecks)
        {
            _ = CheckForUpdatesAsync();
        }
    }

    public void ApplyTheme(AppThemePreference preference)
    {
        if (_window?.RootElement is FrameworkElement root)
        {
            root.RequestedTheme = AppSettings.ToElementTheme(preference);
        }
    }

    private void OnAppInstanceActivated(object? sender, AppActivationArguments e)
    {
        _window?.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            AppLogger.Info("Existing instance activation received.");
            _window.ShowFromTray();
        });
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled UI exception", e.Exception);
    }

    private static async Task CheckForUpdatesAsync()
    {
        try
        {
            await ApplicationUpdateService.Current.CheckAsync();
        }
        catch (Exception exception)
        {
            AppLogger.Info($"Automatic update check failed. {exception.Message}");
        }
    }
}
