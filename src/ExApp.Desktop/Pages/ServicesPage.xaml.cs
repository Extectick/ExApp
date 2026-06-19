using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using ExApp.Desktop.Services;
using ExApp.Ipc;

namespace ExApp.Desktop.Pages;

public sealed partial class ServicesPage : Page, ILocalizedPage
{
    private static string? s_lastSelectedServiceId;

    private readonly AgentServiceClient _serviceClient = new();
    private readonly ServiceCatalogClient _catalogClient = new();
    private readonly ServiceOperationCoordinator _operations = ServiceOperationCoordinator.Current;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Dictionary<string, AgentServiceInfo> _serviceInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServiceCatalogItem> _catalogItems = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedServiceId;
    private bool _isRefreshRunning;
    private bool _agentAvailable = true;
    private bool _suppressNextItemClick;
    private DateTimeOffset _suppressItemClickUntil;
    private bool _isInitialLoading = true;
    private DateTimeOffset _updatedAt = DateTimeOffset.Now;
    private string _serviceSearchText = string.Empty;
    private string _serviceFilter = "all";
    private string _rawLogs = string.Empty;
    private bool _logsPaused;
    private bool _useCompactServiceCardActions;

    internal ObservableCollection<InstalledServiceCard> Services { get; } = [];

    public ServicesPage()
    {
        InitializeComponent();
        Loaded += ServicesPage_Loaded;
        Unloaded += ServicesPage_Unloaded;
        ServiceChangeNotifier.Changed += ServiceChangeNotifier_Changed;
        _operations.Changed += ServiceOperations_Changed;
        _selectedServiceId = s_lastSelectedServiceId;
        _refreshTimer.Tick += RefreshTimer_Tick;
        _notificationTimer.Tick += NotificationTimer_Tick;
        ApplyLocalization();
    }

    public void ApplyLocalization()
    {
        var localization = LocalizationService.Current;
        var selected = GetSelectedService();
        var status = selected?.Status;
        var isDetailsOpen = selected is not null;
        var shouldStop = selected is not null && ShouldStop(selected);
        var isStarting = status?.State.Equals("starting", StringComparison.OrdinalIgnoreCase) == true;
        var selectedOperation = selected is null ? null : _operations.GetKind(selected.ServiceId);
        var isSelectedBusy = selectedOperation is not null;
        var selectedHasUpdate = selected is not null && HasUpdate(selected);

        TitleText.Text = localization.Translate("services.title");
        ToolTipService.SetToolTip(BackButton, localization.Translate("services.mock.back"));
        AutomationProperties.SetName(BackButton, localization.Translate("services.mock.back"));
        ServiceSearchBox.PlaceholderText = localization.Translate("services.search.placeholder");
        SetFilterItems(
            ServiceFilterBox,
            [
                ("all", localization.Translate("services.filter.all")),
                ("running", localization.Translate("services.filter.running")),
                ("stopped", localization.Translate("services.filter.stopped")),
                ("issues", localization.Translate("services.filter.issues")),
                ("updates", localization.Translate("services.filter.updates"))
            ],
            _serviceFilter);
        BackButton.Visibility = isDetailsOpen ? Visibility.Visible : Visibility.Collapsed;
        var isFilteredEmpty = _serviceInfo.Count > 0 && IsFilterActive();
        EmptyServicesTitle.Text = localization.Translate(isFilteredEmpty ? "services.empty.filtered.title" : "services.empty.title");
        EmptyServicesMessage.Text = localization.Translate(isFilteredEmpty ? "services.empty.filtered.message" : "services.empty.message");
        ServicesLoadingText.Text = localization.Translate("services.loading");
        ServicesLoadingPanel.Visibility = _isInitialLoading ? Visibility.Visible : Visibility.Collapsed;
        ServicesLoadingRing.IsActive = _isInitialLoading;
        EmptyServicesPanel.Visibility = !_isInitialLoading && Services.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstalledServicesGrid.Visibility = !_isInitialLoading && Services.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ListViewRoot.Visibility = isDetailsOpen ? Visibility.Collapsed : Visibility.Visible;
        DetailsViewRoot.Visibility = isDetailsOpen ? Visibility.Visible : Visibility.Collapsed;

        if (selected is null || status is null)
        {
            RebuildCards();
            return;
        }

        DetailsServiceNameText.Text = selected.Name;
        DetailsServiceMessageText.Text = status.Message;
        DetailsMetaText.Text = BuildDetailsMetaText(selected, status, localization);
        ApplyDetailsServiceIcon(selected.ServiceId);
        DetailsToggleButtonText.Text = localization.Translate(
            isSelectedBusy ? GetOperationTextKey(selectedOperation) : shouldStop ? "services.mock.stop" : "services.mock.start");
        DetailsToggleIcon.Symbol = shouldStop ? Symbol.Stop : Symbol.Play;
        DetailsStartingRing.IsActive = isStarting || isSelectedBusy;
        DetailsStartingRing.Visibility = isStarting || isSelectedBusy ? Visibility.Visible : Visibility.Collapsed;
        DetailsToggleIcon.Visibility = isStarting || isSelectedBusy ? Visibility.Collapsed : Visibility.Visible;
        DetailsToggleButton.IsEnabled = _agentAvailable && !isSelectedBusy;
        DetailsToggleButton.Tag = selected.ServiceId;

        LogsTitleText.Text = localization.Translate("services.mock.logs");
        UpdatedText.Text = $"{localization.Translate("services.mock.updated")}: {_updatedAt:HH:mm:ss}";
        RefreshButton.IsEnabled = !_isRefreshRunning;
        RefreshLogsButton.IsEnabled = !_isRefreshRunning;
        ClearLogsButton.IsEnabled = !_isRefreshRunning;
        ServiceMenuButton.IsEnabled = !isSelectedBusy;
        RestartMenuItem.Text = localization.Translate("services.restart");
        RestartMenuItem.IsEnabled = shouldStop && !isSelectedBusy;
        UpdateMenuItem.Text = localization.Translate("services.update");
        UpdateMenuItem.Visibility = selectedHasUpdate ? Visibility.Visible : Visibility.Collapsed;
        UninstallMenuItem.Text = localization.Translate("services.mock.uninstall");
        LogsSearchBox.PlaceholderText = localization.Translate("services.logs.search");
        PauseLogsButton.Content = localization.Translate("services.logs.pause");
        ToolTipService.SetToolTip(FollowLogsButton, localization.Translate("services.logs.follow"));
        AutomationProperties.SetName(FollowLogsButton, localization.Translate("services.logs.follow"));
        ToolTipService.SetToolTip(ExportLogsButton, localization.Translate("services.logs.export"));
        AutomationProperties.SetName(ExportLogsButton, localization.Translate("services.logs.export"));
        RebuildCards();
    }

    private async void ServicesPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadCatalogAsync();
        await RefreshAsync(includeLogs: _selectedServiceId is not null);
        _isInitialLoading = false;
        ApplyLocalization();
    }

    private void ServicesPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceChangeNotifier.Changed -= ServiceChangeNotifier_Changed;
        _operations.Changed -= ServiceOperations_Changed;
        _refreshTimer.Stop();
        _notificationTimer.Stop();
    }

    private void ServiceOperations_Changed(object? sender, EventArgs e)
    {
        RebuildCards();
        ApplyLocalization();
    }

    private void NotificationTimer_Tick(object? sender, object e)
    {
        _notificationTimer.Stop();
        ServiceOperationInfoBar.IsOpen = false;
    }

    private async void ServiceChangeNotifier_Changed(object? sender, EventArgs e) =>
        await RefreshAsync(includeLogs: _selectedServiceId is not null);

    private void ServiceSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _serviceSearchText = sender.Text.Trim();
        RebuildCards();
        ApplyListVisibility();
    }

    private void ServiceFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServiceFilterBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _serviceFilter = tag;
            RebuildCards();
            ApplyListVisibility();
        }
    }

    private void InstalledServicesGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (InstalledServicesGrid.ItemsPanelRoot is ItemsWrapGrid panel && e.NewSize.Width > 0)
        {
            var itemWidth = CalculateServiceCardWidth(e.NewSize.Width, Services.Count);
            panel.ItemWidth = itemWidth;
            SetCompactServiceCardActions(itemWidth < 360);
        }
    }

    private void SetCompactServiceCardActions(bool useCompactActions)
    {
        if (_useCompactServiceCardActions == useCompactActions)
        {
            return;
        }

        _useCompactServiceCardActions = useCompactActions;
        foreach (var service in Services)
        {
            service.UseCompactAction = useCompactActions;
        }
    }

    private static double CalculateServiceCardWidth(double availableWidth, int itemCount)
    {
        const double minCardWidth = 320;
        const double preferredCardWidth = 420;

        if (availableWidth <= minCardWidth)
        {
            return Math.Max(0, availableWidth);
        }

        var maxColumnsByContent = Math.Max(1, itemCount);
        var columns = Math.Min(maxColumnsByContent, Math.Max(1, (int)Math.Floor(availableWidth / preferredCardWidth)));
        var itemWidth = Math.Floor(availableWidth / columns);

        return Math.Min(preferredCardWidth, Math.Max(minCardWidth, itemWidth));
    }

    private async void InstalledServicesGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ShouldSuppressCardOpen())
        {
            return;
        }

        if (e.ClickedItem is not InstalledServiceCard card)
        {
            return;
        }

        _selectedServiceId = card.ServiceId;
        s_lastSelectedServiceId = card.ServiceId;
        ApplyLocalization();
        await RefreshAsync(includeLogs: true);
    }

    private async void ListToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string serviceId })
        {
            SuppressCardOpen();
            await ToggleServiceAsync(serviceId);
        }
    }

    private void CardMenuButton_Click(object sender, RoutedEventArgs e)
    {
        SuppressCardOpen();
    }

    private void CardMenuFlyout_Closed(object? sender, object e)
    {
        ClearCardOpenSuppression();
    }

    private async void CardUninstallMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string serviceId })
        {
            SuppressCardOpen();
            await UninstallAsync(serviceId);
        }
    }

    private async void CardUpdateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string serviceId })
        {
            SuppressCardOpen();
            await UpdateServiceAsync(serviceId);
        }
    }

    private void SuppressCardOpen()
    {
        _suppressNextItemClick = true;
        _suppressItemClickUntil = DateTimeOffset.Now.AddMilliseconds(500);
    }

    private bool ShouldSuppressCardOpen()
    {
        if (!_suppressNextItemClick)
        {
            return false;
        }

        if (DateTimeOffset.Now > _suppressItemClickUntil)
        {
            ClearCardOpenSuppression();
            return false;
        }

        ClearCardOpenSuppression();
        return true;
    }

    private void ClearCardOpenSuppression()
    {
        _suppressNextItemClick = false;
        _suppressItemClickUntil = DateTimeOffset.MinValue;
    }

    private async void ToggleServiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedServiceId is not null)
        {
            await ToggleServiceAsync(_selectedServiceId);
        }
    }

    private async Task ToggleServiceAsync(string serviceId)
    {
        if (!_serviceInfo.TryGetValue(serviceId, out var service))
        {
            return;
        }

        try
        {
            var shouldStop = ShouldStop(service);
            await _operations.RunAsync(
                serviceId,
                shouldStop ? ServiceOperationKind.Stop : ServiceOperationKind.Start,
                async _ =>
                {
                    var status = shouldStop
                        ? await _serviceClient.StopAsync(serviceId)
                        : await _serviceClient.StartAsync(serviceId);
                    if (status is not null)
                    {
                        _serviceInfo[serviceId] = service with
                        {
                            Status = status,
                            LifecycleState = ServiceLifecycle.From(service.Installed, status)
                        };
                    }

                    await RefreshAsync(includeLogs: _selectedServiceId == serviceId);
                });
        }
        catch (IpcException exception)
        {
            ApplyAgentError(exception);
        }
        finally
        {
            RebuildCards();
            UpdateTimer();
            ApplyLocalization();
        }
    }

    private async Task UninstallAsync(string serviceId)
    {
        if (!_serviceInfo.TryGetValue(serviceId, out var selected) || _operations.IsActive(serviceId))
        {
            return;
        }

        var localization = LocalizationService.Current;
        var deleteDataCheckBox = new CheckBox
        {
            Content = localization.Translate("services.mock.uninstall.deleteData"),
            IsChecked = false
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = string.Format(localization.Translate("services.uninstall.message"), selected.Name),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(deleteDataCheckBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = string.Format(localization.Translate("services.uninstall.title"), selected.Name),
            Content = content,
            PrimaryButtonText = localization.Translate("services.mock.uninstall.confirm"),
            CloseButtonText = localization.Translate("common.cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _operations.RunAsync(serviceId, ServiceOperationKind.Uninstall, async _ =>
            {
                await _serviceClient.UninstallAsync(selected.ServiceId, deleteDataCheckBox.IsChecked == true);
                _serviceInfo.Remove(selected.ServiceId);
                if (string.Equals(_selectedServiceId, selected.ServiceId, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedServiceId = null;
                    s_lastSelectedServiceId = null;
                }

                ServiceChangeNotifier.NotifyChanged();
                ShowServiceOperation(
                    InfoBarSeverity.Success,
                    localization.Translate("services.mock.uninstall.success.title"),
                    localization.Translate(deleteDataCheckBox.IsChecked == true
                        ? "services.mock.uninstall.success.deleted"
                        : "services.mock.uninstall.success.preserved"));
            });
        }
        catch (IpcException exception)
        {
            ShowServiceOperation(
                InfoBarSeverity.Error,
                localization.Translate("services.mock.uninstall.failed"),
                exception.Message);
        }
        finally
        {
            RebuildCards();
            ApplyLocalization();
        }
    }

    private async Task UpdateServiceAsync(string serviceId)
    {
        if (!_serviceInfo.TryGetValue(serviceId, out var selected) ||
            !_catalogItems.TryGetValue(serviceId, out var catalogItem) ||
            !HasUpdate(selected, catalogItem) ||
            _operations.IsActive(serviceId))
        {
            return;
        }

        var localization = LocalizationService.Current;
        try
        {
            await _operations.RunAsync(serviceId, ServiceOperationKind.Update, async cancellationToken =>
            {
                var package = await _catalogClient.ResolvePackageAsync(catalogItem, selected.Version, cancellationToken);
                try
                {
                    await _serviceClient.UpdateAsync(package.PackagePath, package.Sha256, package.IsDelta);
                }
                catch (IpcException exception) when (package.IsDelta && IsDeltaFallbackCandidate(exception))
                {
                    await UpdateHistoryStore.AddAsync(new UpdateHistoryEntry(
                        DateTimeOffset.Now,
                        selected.Name,
                        catalogItem.Version,
                        "delta-fallback",
                        exception.Message));
                    var fullPackage = await _catalogClient.ResolvePackageAsync(
                        catalogItem,
                        selected.Version,
                        cancellationToken,
                        preferDelta: false);
                    await _serviceClient.UpdateAsync(fullPackage.PackagePath, fullPackage.Sha256, isDelta: false);
                }

                ServiceChangeNotifier.NotifyChanged();
                await LoadCatalogAsync();
                await RefreshAsync(includeLogs: _selectedServiceId == serviceId);
                await UpdateHistoryStore.AddAsync(new UpdateHistoryEntry(
                    DateTimeOffset.Now,
                    selected.Name,
                    catalogItem.Version,
                    "installed",
                    null));
                ShowServiceOperation(
                    InfoBarSeverity.Success,
                    localization.Translate("services.update.success.title"),
                    string.Format(localization.Translate("services.update.success.message"), selected.Name, catalogItem.Version));
            });
        }
        catch (Exception exception) when (
            exception is IpcException or InvalidOperationException or IOException or HttpRequestException)
        {
            await UpdateHistoryStore.AddAsync(new UpdateHistoryEntry(
                DateTimeOffset.Now,
                selected.Name,
                catalogItem.Version,
                exception is IpcException { Code: "service.updateRolledBack" } ? "rolled-back" : "failed",
                exception.Message));
            var titleKey = exception is IpcException { Code: "service.updateRolledBack" }
                ? "services.update.rolledBack.title"
                : exception is IpcException { Code: "service.updateRollbackFailed" or "service.updateRestoreFailed" }
                    ? "services.update.recoveryFailed.title"
                    : "services.update.failed";
            ShowServiceOperation(
                InfoBarSeverity.Error,
                localization.Translate(titleKey),
                exception.Message);
        }
        finally
        {
            RebuildCards();
            ApplyLocalization();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync(includeLogs: _selectedServiceId is not null);

    private void BackButton_Click(object sender, RoutedEventArgs e) => CloseDetailsView();

    private void ServicesPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_selectedServiceId is null || e.Handled)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape ||
            e.Key == VirtualKey.Back && !IsTextInputFocused())
        {
            CloseDetailsView();
            e.Handled = true;
        }
    }

    private void CloseDetailsView()
    {
        if (_selectedServiceId is null)
        {
            return;
        }

        _selectedServiceId = null;
        s_lastSelectedServiceId = null;
        ApplyLocalization();
        UpdateTimer();
    }

    private bool IsTextInputFocused()
    {
        var focusedElement = FocusManager.GetFocusedElement(XamlRoot);
        return focusedElement is TextBox or AutoSuggestBox or PasswordBox;
    }

    private async void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedServiceId is null)
        {
            return;
        }

        try
        {
            await _serviceClient.ClearLogsAsync(_selectedServiceId);
            _rawLogs = string.Empty;
            await RefreshLogsAsync(_selectedServiceId);
        }
        catch (IpcException exception)
        {
            ApplyAgentError(exception);
        }
    }

    private async void UninstallMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedServiceId is not null)
        {
            await UninstallAsync(_selectedServiceId);
        }
    }

    private async void UpdateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedServiceId is not null)
        {
            await UpdateServiceAsync(_selectedServiceId);
        }
    }

    private async void RestartMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedServiceId is null || _operations.IsActive(_selectedServiceId))
        {
            return;
        }

        var serviceId = _selectedServiceId;
        try
        {
            await _operations.RunAsync(serviceId, ServiceOperationKind.Restart, async _ =>
            {
                var status = await _serviceClient.RestartAsync(serviceId);
                if (status is not null && _serviceInfo.TryGetValue(serviceId, out var service))
                {
                    _serviceInfo[serviceId] = service with
                    {
                        Status = status,
                        LifecycleState = ServiceLifecycle.From(service.Installed, status)
                    };
                }

                await RefreshAsync(includeLogs: true);
            });
        }
        catch (IpcException exception)
        {
            ApplyAgentError(exception);
        }
        finally
        {
            ApplyLocalization();
        }
    }

    private async void RefreshTimer_Tick(object? sender, object e) =>
        await RefreshAsync(includeLogs: _selectedServiceId is not null);

    private async Task RefreshAsync(bool includeLogs)
    {
        if (_isRefreshRunning)
        {
            return;
        }

        _isRefreshRunning = true;
        ApplyLocalization();
        try
        {
            var serviceList = await _serviceClient.ListAsync();
            _serviceInfo.Clear();
            foreach (var service in serviceList.Where(service => service.Installed))
            {
                _serviceInfo[service.ServiceId] = service;
            }

            if (_selectedServiceId is not null && !_serviceInfo.ContainsKey(_selectedServiceId))
            {
                _selectedServiceId = null;
                s_lastSelectedServiceId = null;
            }

            _agentAvailable = true;
            _updatedAt = DateTimeOffset.Now;
            RebuildCards();
            if (includeLogs && _selectedServiceId is not null)
            {
                await RefreshLogsAsync(_selectedServiceId);
            }
        }
        catch (IpcException exception)
        {
            ApplyAgentError(exception);
        }
        finally
        {
            _isRefreshRunning = false;
            UpdateTimer();
            ApplyLocalization();
        }
    }

    private async Task LoadCatalogAsync()
    {
        try
        {
            var catalog = await _catalogClient.LoadAsync();
            _catalogItems.Clear();
            foreach (var item in catalog.Services)
            {
                _catalogItems[item.Id] = item;
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or System.Text.Json.JsonException or HttpRequestException)
        {
            AppLogger.Info($"Service update catalog unavailable. {exception.Message}");
        }
    }

    private async Task RefreshLogsAsync(string serviceId)
    {
        if (!_logsPaused)
        {
            _rawLogs = await _serviceClient.GetLogsAsync(serviceId);
        }

        ApplyLogFilter(scrollToEnd: !_logsPaused);
    }

    private void LogsSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyLogFilter(scrollToEnd: false);

    private void PauseLogsButton_Changed(object sender, RoutedEventArgs e)
    {
        _logsPaused = PauseLogsButton.IsChecked == true;
        if (!_logsPaused && _selectedServiceId is not null)
        {
            _ = RefreshLogsAsync(_selectedServiceId);
        }
    }

    private void FollowLogsButton_Click(object sender, RoutedEventArgs e) =>
        LogsTextBox.Select(0, 0);

    private async void ExportLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedServiceId is null)
        {
            return;
        }

        try
        {
            var owner = Application.Current is App { MainWindow: { } mainWindow }
                ? mainWindow.WindowHandle
                : 0;
            var path = NativeSaveFileDialog.Show(
                owner,
                LocalizationService.Current.Translate("services.logs.export"),
                AppSettings.LastLogExportDirectory,
                $"ExApp-{_selectedServiceId}-logs-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await File.WriteAllTextAsync(path, FormatLogLines(_rawLogs, string.Empty));
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                AppSettings.LastLogExportDirectory = directory;
            }

            ShowServiceOperation(
                InfoBarSeverity.Success,
                LocalizationService.Current.Translate("services.logs.exported"),
                path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowServiceOperation(
                InfoBarSeverity.Error,
                LocalizationService.Current.Translate("services.logs.exportFailed"),
                exception.Message);
        }
    }

    private void ApplyLogFilter(bool scrollToEnd)
    {
        var query = LogsSearchBox?.Text.Trim() ?? string.Empty;
        var filtered = FormatLogLines(_rawLogs, query);
        LogsTextBox.Text = string.IsNullOrWhiteSpace(filtered)
            ? LocalizationService.Current.Translate("services.mock.logs.empty")
            : filtered;
        if (scrollToEnd)
        {
            LogsTextBox.Select(0, 0);
        }
    }

    private string FormatLogLines(string logs, string query)
    {
        if (string.IsNullOrWhiteSpace(logs))
        {
            return string.Empty;
        }

        var lines = logs.Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Reverse()
            .Select(FormatLogLine);

        if (!string.IsNullOrWhiteSpace(query))
        {
            lines = lines.Where(line => line.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatLogLine(string line)
    {
        var separatorIndex = line.IndexOf(' ');
        if (separatorIndex <= 0)
        {
            return line;
        }

        var timestamp = line[..separatorIndex];
        if (!DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            return line;
        }

        var message = line[(separatorIndex + 1)..].TrimStart();
        return $"{parsed.ToLocalTime():dd.MM.yyyy HH:mm:ss}  {message}";
    }

    private void ApplyDetailsServiceIcon(string serviceId)
    {
        var serviceIcon = _catalogItems.TryGetValue(serviceId, out var catalogItem)
            ? ServiceIconResolver.Resolve(serviceId, catalogItem.Icon)
            : ServiceIconResolver.Resolve(serviceId, null);

        DetailsServiceIconHost.Visibility =
            serviceIcon.ImageSource is null && string.IsNullOrEmpty(serviceIcon.Glyph)
                ? Visibility.Collapsed
                : Visibility.Visible;
        DetailsServiceIconImage.Source = serviceIcon.ImageSource;
        DetailsServiceIconImage.Visibility = serviceIcon.ImageSource is null ? Visibility.Collapsed : Visibility.Visible;
        DetailsServiceIconGlyph.Glyph = serviceIcon.Glyph ?? string.Empty;
        DetailsServiceIconGlyph.Visibility = string.IsNullOrEmpty(serviceIcon.Glyph) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RebuildCards()
    {
        if (InstalledServicesGrid is null)
        {
            return;
        }

        var localization = LocalizationService.Current;
        var nextCards = _serviceInfo.Values
            .Where(MatchesServiceFilter)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(service =>
            {
                var operation = _operations.GetKind(service.ServiceId);
                var isBusy = operation is not null;
                var shouldStop = ShouldStop(service);
                var hasUpdate = HasUpdate(service);
                var serviceIcon = _catalogItems.TryGetValue(service.ServiceId, out var iconCatalogItem)
                    ? ServiceIconResolver.Resolve(service.ServiceId, iconCatalogItem.Icon)
                    : ServiceIconResolver.Resolve(service.ServiceId, null);
                return new InstalledServiceCard
                {
                    ServiceId = service.ServiceId,
                    Name = service.Name,
                    Message = service.Status.Message,
                    ServiceIcon = serviceIcon,
                    VersionText = $"{localization.Translate("services.mock.version")}: {service.Version}",
                    StateText = TranslateState(service.Status.State),
                    ActionText = localization.Translate(
                        isBusy ? GetOperationTextKey(operation) : shouldStop ? "services.mock.stop" : "services.mock.start"),
                    ActionsText = localization.Translate("services.actions"),
                    UpdateText = hasUpdate && _catalogItems.TryGetValue(service.ServiceId, out var catalogItem)
                        ? string.Format(localization.Translate("services.update.toVersion"), catalogItem.Version)
                        : localization.Translate("services.update"),
                    HasUpdate = hasUpdate,
                    UninstallText = localization.Translate("services.mock.uninstall"),
                    ActionIcon = shouldStop ? Symbol.Stop : Symbol.Play,
                    IsBusy = isBusy,
                    IsActionEnabled = _agentAvailable && !isBusy,
                    UseCompactAction = _useCompactServiceCardActions
                };
            })
            .ToList();

        for (var index = 0; index < nextCards.Count; index++)
        {
            var next = nextCards[index];
            var currentIndex = FindCardIndex(next.ServiceId);
            if (currentIndex < 0)
            {
                Services.Insert(index, next);
                continue;
            }

            if (currentIndex != index)
            {
                Services.Move(currentIndex, index);
            }

            Services[index].UpdateFrom(next);
        }

        for (var index = Services.Count - 1; index >= nextCards.Count; index--)
        {
            Services.RemoveAt(index);
        }

        ApplyListVisibility();
    }

    private int FindCardIndex(string serviceId)
    {
        for (var index = 0; index < Services.Count; index++)
        {
            if (string.Equals(Services[index].ServiceId, serviceId, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private AgentServiceInfo? GetSelectedService() =>
        _selectedServiceId is not null && _serviceInfo.TryGetValue(_selectedServiceId, out var service)
            ? service
            : null;

    private bool HasUpdate(AgentServiceInfo service) =>
        _catalogItems.TryGetValue(service.ServiceId, out var catalogItem) && HasUpdate(service, catalogItem);

    private bool MatchesServiceFilter(AgentServiceInfo service)
    {
        if (!string.IsNullOrWhiteSpace(_serviceSearchText) &&
            !Contains(service.Name, _serviceSearchText) &&
            !Contains(service.ServiceId, _serviceSearchText) &&
            !Contains(service.Status.Message, _serviceSearchText))
        {
            return false;
        }

        return _serviceFilter switch
        {
            "running" => service.LifecycleState.Equals(ServiceLifecycleStates.Running, StringComparison.OrdinalIgnoreCase) ||
                service.LifecycleState.Equals(ServiceLifecycleStates.Starting, StringComparison.OrdinalIgnoreCase),
            "stopped" => service.LifecycleState.Equals(ServiceLifecycleStates.Installed, StringComparison.OrdinalIgnoreCase),
            "issues" => service.LifecycleState.Equals(ServiceLifecycleStates.Failed, StringComparison.OrdinalIgnoreCase),
            "updates" => HasUpdate(service),
            _ => true
        };
    }

    private bool IsFilterActive() =>
        !string.IsNullOrWhiteSpace(_serviceSearchText) ||
        !_serviceFilter.Equals("all", StringComparison.OrdinalIgnoreCase);

    private void ApplyListVisibility()
    {
        if (InstalledServicesGrid is null)
        {
            return;
        }

        var isDetailsOpen = GetSelectedService() is not null;
        EmptyServicesPanel.Visibility = !isDetailsOpen && !_isInitialLoading && Services.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstalledServicesGrid.Visibility = !isDetailsOpen && !_isInitialLoading && Services.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool HasUpdate(AgentServiceInfo service, ServiceCatalogItem catalogItem) =>
        catalogItem.Package is not null &&
        catalogItem.Status.Equals("available", StringComparison.OrdinalIgnoreCase) &&
        CompareVersions(catalogItem.Version, service.Version) > 0;

    private static bool IsDeltaFallbackCandidate(IpcException exception) =>
        exception.Code.StartsWith("delta.", StringComparison.OrdinalIgnoreCase);

    private static int CompareVersions(string left, string right)
    {
        var leftValue = Version.TryParse(left.Split('-', 2)[0], out var parsedLeft)
            ? parsedLeft
            : new Version();
        var rightValue = Version.TryParse(right.Split('-', 2)[0], out var parsedRight)
            ? parsedRight
            : new Version();
        return leftValue.CompareTo(rightValue);
    }

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

    private void ApplyAgentError(IpcException exception)
    {
        _agentAvailable = exception.Code is not "ipc.timeout" and not "ipc.emptyResponse";
        ShowServiceOperation(
            InfoBarSeverity.Error,
            LocalizationService.Current.Translate("services.agentError.title"),
            exception.Message);
    }

    private void ShowServiceOperation(InfoBarSeverity severity, string title, string message)
    {
        ServiceOperationInfoBar.Severity = severity;
        ServiceOperationInfoBar.Title = title;
        ServiceOperationInfoBar.Message = message;
        ServiceOperationInfoBar.IsOpen = true;
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    private void UpdateTimer()
    {
        var hasActiveServices = _serviceInfo.Values.Any(IsActiveState);
        if (_selectedServiceId is not null || hasActiveServices)
        {
            if (!_refreshTimer.IsEnabled)
            {
                _refreshTimer.Start();
            }
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    private string BuildDetailsMetaText(
        AgentServiceInfo service,
        AgentServiceStatus status,
        LocalizationService localization)
    {
        var values = new List<string>
        {
            TranslateState(status.State),
            $"{localization.Translate("services.mock.version")} {service.Version}"
        };

        if (service.Runtime?.ProcessId is { } processId)
        {
            values.Add($"PID {processId}");
        }

        if (service.Runtime?.UptimeSeconds is { } uptimeSeconds)
        {
            values.Add(FormatUptime(uptimeSeconds));
        }

        return string.Join("  /  ", values);
    }

    private string TranslateState(string state)
    {
        var key = state.ToLowerInvariant() switch
        {
            "starting" => "services.mock.state.starting",
            "running" => "services.mock.state.running",
            "stopped" => "services.mock.state.stopped",
            "crashed" => "services.mock.state.crashed",
            "safe-mode" => "services.mock.state.safeMode",
            "missing" => "services.mock.state.missing",
            "error" => "services.mock.state.error",
            _ => string.Empty
        };

        return string.IsNullOrEmpty(key) ? state : LocalizationService.Current.Translate(key);
    }

    private static bool ShouldStop(AgentServiceInfo service) =>
        service.LifecycleState.Equals(ServiceLifecycleStates.Running, StringComparison.OrdinalIgnoreCase) ||
        service.LifecycleState.Equals(ServiceLifecycleStates.Starting, StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveState(AgentServiceInfo service) => ShouldStop(service);

    private static string GetOperationTextKey(ServiceOperationKind? operation) =>
        operation switch
        {
            ServiceOperationKind.Install => "services.operation.installing",
            ServiceOperationKind.Start => "services.operation.starting",
            ServiceOperationKind.Stop => "services.operation.stopping",
            ServiceOperationKind.Restart => "services.operation.restarting",
            ServiceOperationKind.Uninstall => "services.operation.uninstalling",
            ServiceOperationKind.Update => "services.operation.updating",
            _ => "services.operation.working"
        };

    private static string FormatUptime(long? uptimeSeconds)
    {
        if (uptimeSeconds is null)
        {
            return LocalizationService.Current.Translate("common.notAvailable");
        }

        var uptime = TimeSpan.FromSeconds(uptimeSeconds.Value);
        return uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays}d {uptime:hh\\:mm\\:ss}"
            : uptime.ToString(@"hh\:mm\:ss");
    }

    private static class NativeSaveFileDialog
    {
        private const int CancelledHResult = unchecked((int)0x800704C7);

        public static string? Show(nint owner, string title, string initialDirectory, string fileName)
        {
            var dialog = (IFileSaveDialog)(object)new FileSaveDialog();
            var options = FileOpenOptions.OverwritePrompt |
                FileOpenOptions.PathMustExist |
                FileOpenOptions.ForceFileSystem;
            dialog.SetOptions(options);
            dialog.SetTitle(title);
            dialog.SetFileName(fileName);
            dialog.SetDefaultExtension("log");
            dialog.SetFileTypes(
                3,
                [
                    new DialogFilterSpec("Log files (*.log)", "*.log"),
                    new DialogFilterSpec("Text files (*.txt)", "*.txt"),
                    new DialogFilterSpec("All files (*.*)", "*.*")
                ]);

            if (Directory.Exists(initialDirectory) &&
                TryCreateShellItem(initialDirectory, out var folder))
            {
                dialog.SetDefaultFolder(folder);
                dialog.SetFolder(folder);
            }

            var result = dialog.Show(owner);
            if (result == CancelledHResult)
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out var item);
            item.GetDisplayName(ShellItemDisplayName.FileSystemPath, out var pathPointer);
            try
            {
                return Marshal.PtrToStringUni(pathPointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }

        private static bool TryCreateShellItem(string path, out IShellItem shellItem)
        {
            var shellItemId = typeof(IShellItem).GUID;
            var result = SHCreateItemFromParsingName(path, nint.Zero, ref shellItemId, out shellItem);
            return result >= 0;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            string path,
            nint bindContext,
            ref Guid riid,
            out IShellItem shellItem);

        [ComImport]
        [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private sealed class FileSaveDialog;

        [ComImport]
        [Guid("84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileSaveDialog
        {
            [PreserveSig]
            int Show(nint parent);
            void SetFileTypes(uint fileTypesCount, [MarshalAs(UnmanagedType.LPArray)] DialogFilterSpec[] fileTypes);
            void SetFileTypeIndex(uint fileType);
            void GetFileTypeIndex(out uint fileType);
            void Advise(nint events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(FileOpenOptions options);
            void GetOptions(out FileOpenOptions options);
            void SetDefaultFolder(IShellItem folder);
            void SetFolder(IShellItem folder);
            void GetFolder(out IShellItem folder);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(nint filter);
            void SetSaveAsItem(IShellItem item);
            void SetProperties(nint store);
            void SetCollectedProperties(nint list, bool appendDefault);
            void GetProperties(out nint store);
            void ApplyProperties(IShellItem item, nint store, nint owner, nint sink);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(nint bindContext, ref Guid handlerId, ref Guid interfaceId, out nint ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(ShellItemDisplayName displayName, out nint name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem item, uint hint, out int order);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private readonly struct DialogFilterSpec(string name, string spec)
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public readonly string Name = name;

            [MarshalAs(UnmanagedType.LPWStr)]
            public readonly string Spec = spec;
        }

        [Flags]
        private enum FileOpenOptions : uint
        {
            OverwritePrompt = 0x00000002,
            PathMustExist = 0x00000800,
            ForceFileSystem = 0x00000040
        }

        private enum ShellItemDisplayName : uint
        {
            FileSystemPath = 0x80058000
        }
    }
}

internal sealed class InstalledServiceCard : INotifyPropertyChanged
{
    private string _serviceId = string.Empty;
    private string _name = string.Empty;
    private string _message = string.Empty;
    private ServiceIconAsset _serviceIcon = ServiceIconAsset.Empty;
    private string _versionText = string.Empty;
    private string _stateText = string.Empty;
    private string _actionText = string.Empty;
    private string _actionsText = string.Empty;
    private string _updateText = string.Empty;
    private string _uninstallText = string.Empty;
    private Symbol _actionIcon;
    private bool _isBusy;
    private bool _isActionEnabled;
    private bool _hasUpdate;
    private bool _useCompactAction;

    public event PropertyChangedEventHandler? PropertyChanged;

    public required string ServiceId
    {
        get => _serviceId;
        init => _serviceId = value;
    }

    public required string Name
    {
        get => _name;
        init => _name = value;
    }

    public required string Message
    {
        get => _message;
        init => _message = value;
    }

    public ServiceIconAsset ServiceIcon
    {
        get => _serviceIcon;
        init => _serviceIcon = value;
    }

    public ImageSource? ServiceIconSource => ServiceIcon.ImageSource;
    public string ServiceIconGlyph => ServiceIcon.Glyph ?? string.Empty;

    public required string VersionText
    {
        get => _versionText;
        init => _versionText = value;
    }

    public required string StateText
    {
        get => _stateText;
        init => _stateText = value;
    }

    public required string ActionText
    {
        get => _actionText;
        init => _actionText = value;
    }

    public required string ActionsText
    {
        get => _actionsText;
        init => _actionsText = value;
    }

    public required string UpdateText
    {
        get => _updateText;
        init => _updateText = value;
    }

    public required string UninstallText
    {
        get => _uninstallText;
        init => _uninstallText = value;
    }

    public Symbol ActionIcon
    {
        get => _actionIcon;
        init => _actionIcon = value;
    }

    public bool IsBusy
    {
        get => _isBusy;
        init => _isBusy = value;
    }

    public bool IsActionEnabled
    {
        get => _isActionEnabled;
        init => _isActionEnabled = value;
    }

    public bool HasUpdate
    {
        get => _hasUpdate;
        init => _hasUpdate = value;
    }

    public bool UseCompactAction
    {
        get => _useCompactAction;
        set
        {
            if (SetField(ref _useCompactAction, value))
            {
                OnPropertyChanged(nameof(ActionButtonWidth));
                OnPropertyChanged(nameof(ActionButtonPadding));
                OnPropertyChanged(nameof(ActionTextVisibility));
            }
        }
    }

    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconVisibility => IsBusy ? Visibility.Collapsed : Visibility.Visible;
    public double ActionButtonWidth => UseCompactAction ? 42 : 148;
    public Thickness ActionButtonPadding => UseCompactAction ? new Thickness(0) : new Thickness(10, 5, 10, 5);
    public Visibility ActionTextVisibility => UseCompactAction ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UpdateVisibility => HasUpdate ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ServiceIconVisibility => ServiceIcon.ImageSource is null && string.IsNullOrEmpty(ServiceIcon.Glyph)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Visibility ServiceIconImageVisibility => ServiceIcon.ImageSource is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ServiceIconGlyphVisibility => string.IsNullOrEmpty(ServiceIcon.Glyph) ? Visibility.Collapsed : Visibility.Visible;

    public void UpdateFrom(InstalledServiceCard next)
    {
        SetField(ref _name, next.Name, nameof(Name));
        SetField(ref _message, next.Message, nameof(Message));
        if (SetField(ref _serviceIcon, next.ServiceIcon, nameof(ServiceIcon)))
        {
            OnPropertyChanged(nameof(ServiceIconSource));
            OnPropertyChanged(nameof(ServiceIconGlyph));
            OnPropertyChanged(nameof(ServiceIconVisibility));
            OnPropertyChanged(nameof(ServiceIconImageVisibility));
            OnPropertyChanged(nameof(ServiceIconGlyphVisibility));
        }

        SetField(ref _versionText, next.VersionText, nameof(VersionText));
        SetField(ref _stateText, next.StateText, nameof(StateText));
        SetField(ref _actionText, next.ActionText, nameof(ActionText));
        SetField(ref _actionsText, next.ActionsText, nameof(ActionsText));
        SetField(ref _updateText, next.UpdateText, nameof(UpdateText));
        SetField(ref _uninstallText, next.UninstallText, nameof(UninstallText));
        SetField(ref _actionIcon, next.ActionIcon, nameof(ActionIcon));
        if (SetField(ref _isBusy, next.IsBusy, nameof(IsBusy)))
        {
            OnPropertyChanged(nameof(ProgressVisibility));
            OnPropertyChanged(nameof(IconVisibility));
        }

        SetField(ref _isActionEnabled, next.IsActionEnabled, nameof(IsActionEnabled));
        if (SetField(ref _hasUpdate, next.HasUpdate, nameof(HasUpdate)))
        {
            OnPropertyChanged(nameof(UpdateVisibility));
        }

        if (SetField(ref _useCompactAction, next.UseCompactAction, nameof(UseCompactAction)))
        {
            OnPropertyChanged(nameof(ActionButtonWidth));
            OnPropertyChanged(nameof(ActionButtonPadding));
            OnPropertyChanged(nameof(ActionTextVisibility));
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
