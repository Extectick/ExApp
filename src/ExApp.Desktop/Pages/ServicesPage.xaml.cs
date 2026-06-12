using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        BackButtonText.Text = localization.Translate("services.mock.back");
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
        StatusLabelText.Text = localization.Translate("services.mock.statusLabel");
        HealthLabelText.Text = localization.Translate("services.mock.health");
        VersionLabelText.Text = localization.Translate("services.mock.version");
        DetailsStateText.Text = TranslateState(status.State);
        DetailsHealthText.Text = status.Health;
        DetailsVersionText.Text = selected.Version;
        PreviousVersionLabelText.Text = localization.Translate("services.details.previousVersion");
        ProcessIdLabelText.Text = localization.Translate("services.details.processId");
        UptimeLabelText.Text = localization.Translate("services.details.uptime");
        ExecutableLabelText.Text = localization.Translate("services.details.executable");
        DetailsPreviousVersionText.Text = selected.Runtime?.PreviousVersion ?? localization.Translate("common.notAvailable");
        DetailsProcessIdText.Text = selected.Runtime?.ProcessId?.ToString() ?? localization.Translate("common.notAvailable");
        DetailsUptimeText.Text = FormatUptime(selected.Runtime?.UptimeSeconds);
        DetailsExecutableText.Text = selected.Runtime?.ExecutablePath ?? localization.Translate("common.notAvailable");
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
            panel.ItemWidth = Math.Min(456, Math.Max(240, e.NewSize.Width));
        }
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
                var package = await _catalogClient.ResolvePackageAsync(catalogItem, cancellationToken);
                await _serviceClient.UpdateAsync(package.PackagePath, package.Sha256);
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

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedServiceId = null;
        s_lastSelectedServiceId = null;
        ApplyLocalization();
        UpdateTimer();
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
        LogsTextBox.Select(LogsTextBox.Text.Length, 0);

    private async void ExportLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedServiceId is null)
        {
            return;
        }

        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            Directory.CreateDirectory(downloads);
            var path = Path.Combine(
                downloads,
                $"ExApp-{_selectedServiceId}-logs-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            await File.WriteAllTextAsync(path, _rawLogs);
            ShowServiceOperation(
                InfoBarSeverity.Success,
                LocalizationService.Current.Translate("services.logs.exported"),
                path);
        }
        catch (IOException exception)
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
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _rawLogs
            : string.Join(
                Environment.NewLine,
                _rawLogs.Split(Environment.NewLine)
                    .Where(line => line.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        LogsTextBox.Text = string.IsNullOrWhiteSpace(filtered)
            ? LocalizationService.Current.Translate("services.mock.logs.empty")
            : filtered;
        if (scrollToEnd)
        {
            LogsTextBox.Select(LogsTextBox.Text.Length, 0);
        }
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
                    IsActionEnabled = _agentAvailable && !isBusy
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

    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconVisibility => IsBusy ? Visibility.Collapsed : Visibility.Visible;
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
