using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyApp.Desktop.Services;
using MyApp.Ipc;

namespace MyApp.Desktop.Pages;

public sealed partial class ServiceBrowserPage : Page, ILocalizedPage
{
    private readonly AgentServiceClient _agentClient = new();
    private bool _isInstalled;
    private bool _isBusy;

    public ServiceBrowserPage()
    {
        InitializeComponent();
        Loaded += ServiceBrowserPage_Loaded;
        Unloaded += ServiceBrowserPage_Unloaded;
        ServiceChangeNotifier.Changed += ServiceChangeNotifier_Changed;
        ApplyLocalization();
    }

    public void ApplyLocalization()
    {
        var localization = LocalizationService.Current;
        TitleText.Text = localization.Translate("browser.title");
        SubtitleText.Text = localization.Translate("browser.subtitle");
        VpnNameText.Text = localization.Translate("browser.vpn.name");
        VpnDescriptionText.Text = localization.Translate("browser.vpn.description");
        VpnActionButton.Content = localization.Translate("browser.vpn.action");

        MockNameText.Text = localization.Translate("browser.mock.name");
        MockPublisherText.Text = localization.Translate("browser.mock.publisher");
        MockDescriptionText.Text = localization.Translate("browser.mock.description");
        MockVersionText.Text = $"{localization.Translate("browser.version")}: 0.1.0";
        MockCategoryText.Text = localization.Translate("browser.mock.category");
        MockStateText.Text = localization.Translate(_isInstalled ? "browser.mock.installed" : "browser.mock.notInstalled");
        MockDetailsButtonText.Text = localization.Translate("browser.details");
        MockActionText.Text = localization.Translate(_isInstalled ? "browser.mock.open" : "browser.mock.install");
        MockActionIcon.Symbol = _isInstalled ? Symbol.OpenFile : Symbol.Download;
        MockActionButton.IsEnabled = !_isBusy;
        MockDetailsButton.IsEnabled = !_isBusy;
        MockActionProgress.IsActive = _isBusy;
        MockActionProgress.Visibility = _isBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ServiceBrowserPage_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private void ServiceBrowserPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceChangeNotifier.Changed -= ServiceChangeNotifier_Changed;
    }

    private async void ServiceChangeNotifier_Changed(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    private async void MockDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowDetailsDialogAsync(allowInstall: !_isInstalled);
    }

    private async void MockActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalled)
        {
            (Application.Current as App)?.MainWindow?.NavigateToServices();
            return;
        }

        await ShowDetailsDialogAsync(allowInstall: true);
    }

    private async Task ShowDetailsDialogAsync(bool allowInstall)
    {
        var localization = LocalizationService.Current;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = localization.Translate("browser.mock.name"),
            CloseButtonText = localization.Translate("common.close"),
            DefaultButton = ContentDialogButton.Primary,
            Content = CreateDetailsContent()
        };

        if (allowInstall)
        {
            dialog.PrimaryButtonText = localization.Translate("browser.mock.install");
        }

        var result = await dialog.ShowAsync();
        if (allowInstall && result == ContentDialogResult.Primary)
        {
            await InstallAsync();
        }
    }

    private UIElement CreateDetailsContent()
    {
        var localization = LocalizationService.Current;
        var content = new StackPanel { Spacing = 16, MaxWidth = 520 };
        content.Children.Add(new TextBlock
        {
            Text = localization.Translate("browser.mock.description"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.82
        });

        var metadata = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddMetadataRow(metadata, 0, localization.Translate("browser.version"), "0.1.0");
        AddMetadataRow(metadata, 1, localization.Translate("browser.publisher"), "ExApp");
        AddMetadataRow(metadata, 2, localization.Translate("browser.source"), localization.Translate("browser.source.local"));
        content.Children.Add(metadata);

        content.Children.Add(new TextBlock
        {
            Text = localization.Translate("browser.permissions"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        var permission = new Grid { ColumnSpacing = 10 };
        permission.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        permission.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        permission.Children.Add(new SymbolIcon(Symbol.Sync) { VerticalAlignment = VerticalAlignment.Center });
        var permissionText = new StackPanel { Spacing = 2 };
        Grid.SetColumn(permissionText, 1);
        permissionText.Children.Add(new TextBlock
        {
            Text = localization.Translate("browser.permission.background"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        permissionText.Children.Add(new TextBlock
        {
            Text = localization.Translate("browser.permission.background.description"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72
        });
        permission.Children.Add(permissionText);
        content.Children.Add(permission);
        return content;
    }

    private async Task InstallAsync()
    {
        _isBusy = true;
        ApplyLocalization();
        ShowOperation(InfoBarSeverity.Informational, "browser.installing.title", "browser.installing.message");

        try
        {
            var packagePath = FindMockPackage()
                ?? throw new InvalidOperationException(LocalizationService.Current.Translate("browser.mock.packageMissing"));
            await _agentClient.InstallAsync(packagePath);
            _isInstalled = true;
            ServiceChangeNotifier.NotifyChanged();
            ShowOperation(InfoBarSeverity.Success, "browser.installed.title", "browser.installed.message");
        }
        catch (Exception exception) when (exception is IpcException or InvalidOperationException)
        {
            OperationInfoBar.Severity = InfoBarSeverity.Error;
            OperationInfoBar.Title = LocalizationService.Current.Translate("browser.installFailed.title");
            OperationInfoBar.Message = exception.Message;
            OperationInfoBar.IsOpen = true;
        }
        finally
        {
            _isBusy = false;
            ApplyLocalization();
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var services = await _agentClient.ListAsync();
            _isInstalled = services.Any(item => item.ServiceId == "mock-service" && item.Installed);
        }
        catch (IpcException exception)
        {
            OperationInfoBar.Severity = InfoBarSeverity.Error;
            OperationInfoBar.Title = LocalizationService.Current.Translate("browser.agentError.title");
            OperationInfoBar.Message = exception.Message;
            OperationInfoBar.IsOpen = true;
        }

        ApplyLocalization();
    }

    private void ShowOperation(InfoBarSeverity severity, string titleKey, string messageKey)
    {
        var localization = LocalizationService.Current;
        OperationInfoBar.Severity = severity;
        OperationInfoBar.Title = localization.Translate(titleKey);
        OperationInfoBar.Message = localization.Translate(messageKey);
        OperationInfoBar.IsOpen = true;
    }

    private static void AddMetadataRow(Grid grid, int row, string label, string value)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var labelText = new TextBlock { Text = label, Opacity = 0.68 };
        var valueText = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(labelText, row);
        Grid.SetRow(valueText, row);
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(labelText);
        grid.Children.Add(valueText);
    }

    private static string? FindMockPackage()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "artifacts", "mock-service-0.1.0-win-x64.svcpkg");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }
}
