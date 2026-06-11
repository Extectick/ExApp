using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyApp.Desktop.Pages;
using MyApp.Desktop.Services;
using System.Runtime.InteropServices;

namespace MyApp.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly TrayIcon _trayIcon;
    private readonly nint _windowHandle;
    private bool _isExitRequested;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        Title = "ExApp";
        AppWindow.Title = "ExApp";
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Closing += AppWindow_Closing;

        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        _trayIcon = new TrayIcon(_windowHandle, iconPath, "ExApp");
        LocalizationService.Current.LanguageChanged += OnLanguageChanged;
        ApplyLocalization();

        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        ContentFrame.Navigate(typeof(ServicesPage));
    }

    public FrameworkElement RootElement => RootGrid;

    public void ApplyLocalization()
    {
        var localization = LocalizationService.Current;
        var title = localization.Translate("app.title");

        Title = title;
        AppWindow.Title = title;
        TitleBarText.Text = title;
        RootNavigation.PaneTitle = string.Empty;
        ServicesNavItem.Content = localization.Translate("nav.myServices");
        BrowserNavItem.Content = localization.Translate("nav.serviceBrowser");
        DiagnosticsNavItem.Content = localization.Translate("nav.diagnostics");
        SettingsNavItem.Content = localization.Translate("nav.settings");

        if (ContentFrame.Content is ILocalizedPage localizedPage)
        {
            localizedPage.ApplyLocalization();
        }
    }

    public void ShowFromTray()
    {
        AppLogger.Info("Showing main window.");
        ShowWindow(_windowHandle, ShowWindowCommand.Show);
        Activate();
    }

    public void NavigateToServices()
    {
        RootNavigation.SelectedItem = ServicesNavItem;
        if (ContentFrame.CurrentSourcePageType != typeof(ServicesPage))
        {
            ContentFrame.Navigate(typeof(ServicesPage));
        }
    }

    public void ExitApplication()
    {
        AppLogger.Info("Application exit requested.");
        _isExitRequested = true;
        Close();
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_isExitRequested)
        {
            _trayIcon.Dispose();
            LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
            return;
        }

        args.Cancel = true;
        AppLogger.Info("Close requested; hiding main window to tray.");
        ShowWindow(_windowHandle, ShowWindowCommand.Hide);
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        var pageType = tag switch
        {
            "services" => typeof(ServicesPage),
            "browser" => typeof(ServiceBrowserPage),
            "settings" => typeof(SettingsPage),
            "diagnostics" => typeof(DiagnosticsPage),
            _ => typeof(ServicesPage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            AppLogger.Info($"Navigating to {tag}.");
            ContentFrame.Navigate(pageType);
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ApplyLocalization();
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, ShowWindowCommand nCmdShow);

    private enum ShowWindowCommand
    {
        Hide = 0,
        Show = 5
    }
}
