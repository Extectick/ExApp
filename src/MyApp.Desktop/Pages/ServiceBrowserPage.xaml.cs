using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyApp.Desktop.Services;
using MyApp.Ipc;

namespace MyApp.Desktop.Pages;

public sealed partial class ServiceBrowserPage : Page, ILocalizedPage
{
    private readonly AgentServiceClient _agentClient = new();
    private readonly ServiceCatalogClient _catalogClient = new();
    private readonly List<ServiceCatalogItem> _catalogItems = [];
    private readonly HashSet<string> _installedServiceIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _busyServiceId;

    internal ObservableCollection<ServiceCatalogCard> Services { get; } = [];

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
        RebuildCards();
    }

    private async void ServiceBrowserPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadCatalogAsync();
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

    private void ServicesGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ServicesGrid.ItemsPanelRoot is not ItemsWrapGrid panel || e.NewSize.Width <= 0)
        {
            return;
        }

        panel.ItemWidth = Math.Min(364, Math.Max(220, e.NewSize.Width));
    }

    private async void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string serviceId } && FindService(serviceId) is { } item)
        {
            await ShowDetailsDialogAsync(item);
        }
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string serviceId } || FindService(serviceId) is not { } item)
        {
            return;
        }

        if (_installedServiceIds.Contains(item.Id))
        {
            (Application.Current as App)?.MainWindow?.NavigateToServices();
            return;
        }

        await InstallAsync(item);
    }

    private async Task ShowDetailsDialogAsync(ServiceCatalogItem item)
    {
        var localization = LocalizationService.Current;
        var isInstallable = IsInstallable(item) && !_installedServiceIds.Contains(item.Id);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = item.Name,
            CloseButtonText = localization.Translate("common.close"),
            DefaultButton = ContentDialogButton.Primary,
            Content = CreateDetailsContent(item)
        };

        if (isInstallable)
        {
            dialog.PrimaryButtonText = localization.Translate("browser.install");
        }

        var result = await dialog.ShowAsync();
        if (isInstallable && result == ContentDialogResult.Primary)
        {
            await InstallAsync(item);
        }
    }

    private UIElement CreateDetailsContent(ServiceCatalogItem item)
    {
        var localization = LocalizationService.Current;
        var content = new StackPanel { Spacing = 16, MaxWidth = 520 };
        content.Children.Add(new TextBlock
        {
            Text = item.Description,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.82
        });

        var metadata = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddMetadataRow(metadata, 0, localization.Translate("browser.version"), item.Version);
        AddMetadataRow(metadata, 1, localization.Translate("browser.publisher"), item.Publisher.Name);
        AddMetadataRow(metadata, 2, localization.Translate("browser.source"), GetSourceLabel(item));
        content.Children.Add(metadata);

        content.Children.Add(new TextBlock
        {
            Text = localization.Translate("browser.permissions"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        if (item.Permissions.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = localization.Translate("browser.permissions.none"),
                Opacity = 0.72
            });
        }
        else
        {
            foreach (var permission in item.Permissions)
            {
                content.Children.Add(CreatePermissionRow(permission));
            }
        }

        return content;
    }

    private static UIElement CreatePermissionRow(string permission)
    {
        var localization = LocalizationService.Current;
        var titleKey = $"browser.permission.{permission}";
        var descriptionKey = $"{titleKey}.description";
        var translatedTitle = localization.Translate(titleKey);
        var translatedDescription = localization.Translate(descriptionKey);

        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new SymbolIcon(Symbol.Permissions) { VerticalAlignment = VerticalAlignment.Center });

        var text = new StackPanel { Spacing = 2 };
        Grid.SetColumn(text, 1);
        text.Children.Add(new TextBlock
        {
            Text = translatedTitle == titleKey ? permission : translatedTitle,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        if (translatedDescription != descriptionKey)
        {
            text.Children.Add(new TextBlock
            {
                Text = translatedDescription,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72
            });
        }

        row.Children.Add(text);
        return row;
    }

    private async Task InstallAsync(ServiceCatalogItem item)
    {
        _busyServiceId = item.Id;
        RebuildCards();
        ShowOperation(
            InfoBarSeverity.Informational,
            LocalizationService.Current.Translate("browser.installing.title"),
            string.Format(LocalizationService.Current.Translate("browser.installing.message"), item.Name));

        try
        {
            var package = await _catalogClient.ResolvePackageAsync(item);
            await _agentClient.InstallAsync(package.PackagePath, package.Sha256);
            _installedServiceIds.Add(item.Id);
            ServiceChangeNotifier.NotifyChanged();
            ShowOperation(
                InfoBarSeverity.Success,
                LocalizationService.Current.Translate("browser.installed.title"),
                string.Format(LocalizationService.Current.Translate("browser.installed.message"), item.Name));
        }
        catch (Exception exception) when (
            exception is IpcException or InvalidOperationException or IOException or HttpRequestException)
        {
            ShowOperation(
                InfoBarSeverity.Error,
                LocalizationService.Current.Translate("browser.installFailed.title"),
                exception.Message);
        }
        finally
        {
            _busyServiceId = null;
            RebuildCards();
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var services = await _agentClient.ListAsync();
            _installedServiceIds.Clear();
            foreach (var service in services.Where(service => service.Installed))
            {
                _installedServiceIds.Add(service.ServiceId);
            }
        }
        catch (IpcException exception)
        {
            ShowOperation(
                InfoBarSeverity.Error,
                LocalizationService.Current.Translate("browser.agentError.title"),
                exception.Message);
        }

        RebuildCards();
    }

    private async Task LoadCatalogAsync()
    {
        try
        {
            var catalog = await _catalogClient.LoadAsync();
            _catalogItems.Clear();
            _catalogItems.AddRange(catalog.Services.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase));
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or System.Text.Json.JsonException or HttpRequestException)
        {
            ShowOperation(
                InfoBarSeverity.Error,
                LocalizationService.Current.Translate("browser.catalogError.title"),
                exception.Message);
        }

        RebuildCards();
    }

    private void RebuildCards()
    {
        if (ServicesGrid is null)
        {
            return;
        }

        var localization = LocalizationService.Current;
        Services.Clear();
        foreach (var item in _catalogItems)
        {
            var isInstalled = _installedServiceIds.Contains(item.Id);
            var isBusy = string.Equals(_busyServiceId, item.Id, StringComparison.OrdinalIgnoreCase);
            var isInstallable = IsInstallable(item);
            Services.Add(new ServiceCatalogCard
            {
                Item = item,
                StateText = localization.Translate(isInstalled
                    ? "browser.installed"
                    : isInstallable ? "browser.notInstalled" : "browser.unavailable"),
                VersionText = $"{localization.Translate("browser.version")}: {item.Version}",
                DetailsText = localization.Translate("browser.details"),
                ActionText = localization.Translate(isInstalled
                    ? "browser.open"
                    : isBusy ? "browser.installing" : isInstallable ? "browser.install" : "browser.unavailable"),
                ActionIcon = isInstalled ? Symbol.OpenFile : Symbol.Download,
                IsBusy = isBusy,
                IsDetailsEnabled = _busyServiceId is null,
                IsActionEnabled = _busyServiceId is null && (isInstalled || isInstallable)
            });
        }
    }

    private ServiceCatalogItem? FindService(string serviceId) =>
        _catalogItems.FirstOrDefault(item => item.Id.Equals(serviceId, StringComparison.OrdinalIgnoreCase));

    private static bool IsInstallable(ServiceCatalogItem item) =>
        item.Package is not null &&
        item.Status.Equals("available", StringComparison.OrdinalIgnoreCase);

    private void ShowOperation(InfoBarSeverity severity, string title, string message)
    {
        OperationInfoBar.Severity = severity;
        OperationInfoBar.Title = title;
        OperationInfoBar.Message = message;
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

    private static string GetSourceLabel(ServiceCatalogItem item) =>
        item.Package?.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true
            ? LocalizationService.Current.Translate("browser.source.github")
            : LocalizationService.Current.Translate("browser.source.local");
}

internal sealed record ServiceCatalogCard
{
    public required ServiceCatalogItem Item { get; init; }
    public required string StateText { get; init; }
    public required string VersionText { get; init; }
    public required string DetailsText { get; init; }
    public required string ActionText { get; init; }
    public Symbol ActionIcon { get; init; }
    public bool IsBusy { get; init; }
    public bool IsDetailsEnabled { get; init; }
    public bool IsActionEnabled { get; init; }
    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconVisibility => IsBusy ? Visibility.Collapsed : Visibility.Visible;
}
