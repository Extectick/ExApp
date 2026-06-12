using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ExApp.Desktop.Services;
using ExApp.Ipc;

namespace ExApp.Desktop.Pages;

public sealed partial class ServiceBrowserPage : Page, ILocalizedPage
{
    private readonly AgentServiceClient _agentClient = new();
    private readonly ServiceCatalogClient _catalogClient = new();
    private readonly ServiceOperationCoordinator _operations = ServiceOperationCoordinator.Current;
    private readonly DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly List<ServiceCatalogItem> _catalogItems = [];
    private readonly HashSet<string> _installedServiceIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _isInitialLoading = true;
    private bool _isCatalogRefreshing;
    private ServiceCatalogLoadResult? _catalogLoadResult;
    private string _browserSearchText = string.Empty;
    private string _browserFilter = "all";

    internal ObservableCollection<ServiceCatalogCard> Services { get; } = [];

    public ServiceBrowserPage()
    {
        InitializeComponent();
        Loaded += ServiceBrowserPage_Loaded;
        Unloaded += ServiceBrowserPage_Unloaded;
        ServiceChangeNotifier.Changed += ServiceChangeNotifier_Changed;
        _operations.Changed += ServiceOperations_Changed;
        _notificationTimer.Tick += NotificationTimer_Tick;
        ApplyLocalization();
    }

    public void ApplyLocalization()
    {
        var localization = LocalizationService.Current;
        TitleText.Text = localization.Translate("browser.title");
        SubtitleText.Text = localization.Translate("browser.subtitle");
        BrowserSearchBox.PlaceholderText = localization.Translate("browser.search.placeholder");
        SetFilterItems(
            BrowserFilterBox,
            [
                ("all", localization.Translate("browser.filter.all")),
                ("available", localization.Translate("browser.filter.available")),
                ("installed", localization.Translate("browser.filter.installed")),
                ("planned", localization.Translate("browser.filter.planned"))
            ],
            _browserFilter);
        BrowserLoadingText.Text = localization.Translate("browser.loading");
        ToolTipService.SetToolTip(RefreshCatalogButton, localization.Translate("browser.refresh"));
        UpdateCatalogStatus();
        var isFilteredEmpty = _catalogItems.Count > 0 && IsFilterActive();
        BrowserEmptyTitle.Text = localization.Translate(isFilteredEmpty ? "browser.empty.filtered.title" : "browser.empty.title");
        BrowserEmptyMessage.Text = localization.Translate(isFilteredEmpty ? "browser.empty.filtered.message" : "browser.empty.message");
        RebuildCards();
    }

    private async void ServiceBrowserPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadCatalogAsync();
        await RefreshAsync();
        _isInitialLoading = false;
        RebuildCards();
    }

    private async void RefreshCatalogButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadCatalogAsync(forceRefresh: true);
    }

    private void ServiceBrowserPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceChangeNotifier.Changed -= ServiceChangeNotifier_Changed;
        _operations.Changed -= ServiceOperations_Changed;
        _notificationTimer.Stop();
    }

    private void ServiceOperations_Changed(object? sender, EventArgs e) => RebuildCards();

    private void NotificationTimer_Tick(object? sender, object e)
    {
        _notificationTimer.Stop();
        OperationInfoBar.IsOpen = false;
    }

    private async void ServiceChangeNotifier_Changed(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    private void BrowserSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _browserSearchText = sender.Text.Trim();
        RebuildCards();
    }

    private void BrowserFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BrowserFilterBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _browserFilter = tag;
            RebuildCards();
        }
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

        await InstallAsync(item, showErrors: true);
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
            await InstallAsync(item, showErrors: true);
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
        OperationInfoBar.IsOpen = false;
        _notificationTimer.Stop();

        await _operations.RunAsync(item.Id, ServiceOperationKind.Install, async cancellationToken =>
        {
            var package = await _catalogClient.ResolvePackageAsync(item, cancellationToken);
            await _agentClient.InstallAsync(package.PackagePath, package.Sha256);
            _installedServiceIds.Add(item.Id);
            ServiceChangeNotifier.NotifyChanged();
        });
    }

    private async Task InstallAsync(ServiceCatalogItem item, bool showErrors)
    {
        try
        {
            await InstallAsync(item);
        }
        catch (Exception exception) when (
            exception is IpcException or InvalidOperationException or IOException or HttpRequestException)
        {
            if (showErrors)
            {
                ShowOperation(
                    InfoBarSeverity.Error,
                    LocalizationService.Current.Translate("browser.installFailed.title"),
                    exception.Message);
            }
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

    private async Task LoadCatalogAsync(bool forceRefresh = false)
    {
        if (_isCatalogRefreshing)
        {
            return;
        }

        _isCatalogRefreshing = true;
        RefreshCatalogButton.IsEnabled = false;
        RefreshCatalogIcon.Visibility = Visibility.Collapsed;
        RefreshCatalogRing.Visibility = Visibility.Visible;
        RefreshCatalogRing.IsActive = true;
        try
        {
            _catalogLoadResult = await _catalogClient.LoadWithMetadataAsync(forceRefresh);
            _catalogItems.Clear();
            _catalogItems.AddRange(_catalogLoadResult.Catalog.Services.OrderBy(
                item => item.Name,
                StringComparer.CurrentCultureIgnoreCase));
            UpdateCatalogStatus();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or System.Text.Json.JsonException or HttpRequestException)
        {
            ShowOperation(
                InfoBarSeverity.Error,
                LocalizationService.Current.Translate("browser.catalogError.title"),
                exception.Message);
        }
        finally
        {
            _isCatalogRefreshing = false;
            RefreshCatalogButton.IsEnabled = true;
            RefreshCatalogIcon.Visibility = Visibility.Visible;
            RefreshCatalogRing.Visibility = Visibility.Collapsed;
            RefreshCatalogRing.IsActive = false;
        }

        RebuildCards();
    }

    private void UpdateCatalogStatus()
    {
        if (CatalogStatusText is null)
        {
            return;
        }

        if (_catalogLoadResult is null)
        {
            CatalogStatusText.Text = string.Empty;
            return;
        }

        var localization = LocalizationService.Current;
        var sourceKey = _catalogLoadResult.Source switch
        {
            ServiceCatalogSource.Remote => "browser.catalog.source.remote",
            ServiceCatalogSource.Override => "browser.catalog.source.override",
            ServiceCatalogSource.Cache => _catalogLoadResult.IsOffline
                ? "browser.catalog.source.cacheOffline"
                : "browser.catalog.source.cache",
            _ => "browser.catalog.source.bundled"
        };
        CatalogStatusText.Text = string.Format(
            localization.Translate("browser.catalog.status"),
            localization.Translate(sourceKey),
            _catalogLoadResult.LoadedAt.ToLocalTime().ToString("g"));
    }

    private void RebuildCards()
    {
        if (ServicesGrid is null)
        {
            return;
        }

        var localization = LocalizationService.Current;
        Services.Clear();
        foreach (var item in _catalogItems.Where(MatchesBrowserFilter))
        {
            var isInstalled = _installedServiceIds.Contains(item.Id);
            var operation = _operations.GetKind(item.Id);
            var isBusy = operation is not null;
            var isInstallable = IsInstallable(item);
            Services.Add(new ServiceCatalogCard
            {
                Item = item,
                StateText = localization.Translate(isInstalled
                    ? "browser.installed"
                    : isInstallable ? "browser.notInstalled" : "browser.unavailable"),
                VersionText = $"{localization.Translate("browser.version")}: {item.Version}",
                ServiceIcon = ServiceIconResolver.Resolve(item.Id, item.Icon),
                DetailsText = localization.Translate("browser.details"),
                ActionText = localization.Translate(isInstalled
                    ? "browser.open"
                    : isBusy ? TranslateOperation(operation) : isInstallable ? "browser.install" : "browser.unavailable"),
                ActionIcon = isInstalled ? Symbol.OpenFile : Symbol.Download,
                IsBusy = isBusy,
                IsDetailsEnabled = !isBusy,
                IsActionEnabled = !isBusy && (isInstalled || isInstallable)
            });
        }

        BrowserLoadingPanel.Visibility = _isInitialLoading ? Visibility.Visible : Visibility.Collapsed;
        BrowserLoadingRing.IsActive = _isInitialLoading;
        BrowserEmptyPanel.Visibility = !_isInitialLoading && Services.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ServicesGrid.Visibility = !_isInitialLoading && Services.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ServiceCatalogItem? FindService(string serviceId) =>
        _catalogItems.FirstOrDefault(item => item.Id.Equals(serviceId, StringComparison.OrdinalIgnoreCase));

    private bool MatchesBrowserFilter(ServiceCatalogItem item)
    {
        if (!string.IsNullOrWhiteSpace(_browserSearchText) &&
            !Contains(item.Name, _browserSearchText) &&
            !Contains(item.Id, _browserSearchText) &&
            !Contains(item.Description, _browserSearchText) &&
            !Contains(item.Category, _browserSearchText) &&
            !Contains(item.Publisher.Name, _browserSearchText))
        {
            return false;
        }

        var isInstalled = _installedServiceIds.Contains(item.Id);
        return _browserFilter switch
        {
            "available" => !isInstalled && IsInstallable(item),
            "installed" => isInstalled,
            "planned" => !IsInstallable(item),
            _ => true
        };
    }

    private bool IsFilterActive() =>
        !string.IsNullOrWhiteSpace(_browserSearchText) ||
        !_browserFilter.Equals("all", StringComparison.OrdinalIgnoreCase);

    private static bool IsInstallable(ServiceCatalogItem item) =>
        item.Package is not null &&
        item.Status.Equals("available", StringComparison.OrdinalIgnoreCase);

    private static void SetFilterItems(
        ComboBox comboBox,
        IReadOnlyList<(string Tag, string Text)> items,
        string selectedTag)
    {
        var existingTag = comboBox.SelectedItem is ComboBoxItem { Tag: string currentTag } ? currentTag : selectedTag;
        comboBox.Items.Clear();
        foreach (var item in items)
        {
            comboBox.Items.Add(new ComboBoxItem { Tag = item.Tag, Content = item.Text });
        }

        for (var index = 0; index < comboBox.Items.Count; index++)
        {
            if (comboBox.Items[index] is ComboBoxItem { Tag: string tag } &&
                tag.Equals(existingTag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = index;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static bool Contains(string value, string query) =>
        value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static string TranslateOperation(ServiceOperationKind? operation) =>
        operation switch
        {
            ServiceOperationKind.Install => LocalizationService.Current.Translate("services.operation.installing"),
            ServiceOperationKind.Update => LocalizationService.Current.Translate("services.operation.updating"),
            _ => LocalizationService.Current.Translate("browser.installing")
        };

    private void ShowOperation(InfoBarSeverity severity, string title, string message)
    {
        OperationInfoBar.Severity = severity;
        OperationInfoBar.Title = title;
        OperationInfoBar.Message = message;
        OperationInfoBar.IsOpen = true;
        _notificationTimer.Stop();
        _notificationTimer.Start();
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
    public ServiceIconAsset ServiceIcon { get; init; } = ServiceIconAsset.Empty;
    public ImageSource? ServiceIconSource => ServiceIcon.ImageSource;
    public string ServiceIconGlyph => ServiceIcon.Glyph ?? string.Empty;
    public required string DetailsText { get; init; }
    public required string ActionText { get; init; }
    public Symbol ActionIcon { get; init; }
    public bool IsBusy { get; init; }
    public bool IsDetailsEnabled { get; init; }
    public bool IsActionEnabled { get; init; }
    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconVisibility => IsBusy ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ServiceIconVisibility => ServiceIcon.ImageSource is null && string.IsNullOrEmpty(ServiceIcon.Glyph)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Visibility ServiceIconImageVisibility => ServiceIcon.ImageSource is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ServiceIconGlyphVisibility => string.IsNullOrEmpty(ServiceIcon.Glyph) ? Visibility.Collapsed : Visibility.Visible;
}
