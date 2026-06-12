using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyApp.Desktop.Services;
using MyApp.Ipc;

namespace MyApp.Desktop.Pages;

public sealed partial class ServicesPage : Page, ILocalizedPage
{
    private static string? s_lastSelectedServiceId;

    private readonly AgentServiceClient _serviceClient = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, AgentServiceInfo> _serviceInfo = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedServiceId;
    private string? _busyServiceId;
    private bool _isRefreshRunning;
    private bool _agentAvailable = true;
    private bool _suppressNextItemClick;
    private DateTimeOffset _updatedAt = DateTimeOffset.Now;

    internal ObservableCollection<InstalledServiceCard> Services { get; } = [];

    public ServicesPage()
    {
        InitializeComponent();
        Loaded += ServicesPage_Loaded;
        Unloaded += ServicesPage_Unloaded;
        ServiceChangeNotifier.Changed += ServiceChangeNotifier_Changed;
        _selectedServiceId = s_lastSelectedServiceId;
        _refreshTimer.Tick += RefreshTimer_Tick;
        ApplyLocalization();
    }

    public void ApplyLocalization()
    {
        var localization = LocalizationService.Current;
        var selected = GetSelectedService();
        var status = selected?.Status;
        var isDetailsOpen = selected is not null;
        var shouldStop = status is not null && ShouldStop(status.State);
        var isStarting = status?.State.Equals("starting", StringComparison.OrdinalIgnoreCase) == true;

        TitleText.Text = localization.Translate("services.title");
        BackButtonText.Text = localization.Translate("services.mock.back");
        BackButton.Visibility = isDetailsOpen ? Visibility.Visible : Visibility.Collapsed;
        EmptyServicesTitle.Text = localization.Translate("services.empty.title");
        EmptyServicesMessage.Text = localization.Translate("services.empty.message");
        EmptyServicesPanel.Visibility = Services.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstalledServicesGrid.Visibility = Services.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
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
        DetailsToggleButtonText.Text = localization.Translate(
            shouldStop ? "services.mock.stop" : "services.mock.start");
        DetailsToggleIcon.Symbol = shouldStop ? Symbol.Stop : Symbol.Play;
        DetailsStartingRing.IsActive = isStarting;
        DetailsStartingRing.Visibility = isStarting ? Visibility.Visible : Visibility.Collapsed;
        DetailsToggleIcon.Visibility = isStarting ? Visibility.Collapsed : Visibility.Visible;
        DetailsToggleButton.IsEnabled = _agentAvailable && _busyServiceId is null;
        DetailsToggleButton.Tag = selected.ServiceId;

        LogsTitleText.Text = localization.Translate("services.mock.logs");
        UpdatedText.Text = $"{localization.Translate("services.mock.updated")}: {_updatedAt:HH:mm:ss}";
        RefreshButton.IsEnabled = !_isRefreshRunning;
        RefreshLogsButton.IsEnabled = !_isRefreshRunning;
        ClearLogsButton.IsEnabled = !_isRefreshRunning;
        ServiceMenuButton.IsEnabled = _busyServiceId is null;
        UninstallMenuItem.Text = localization.Translate("services.mock.uninstall");
        RebuildCards();
    }

    private async void ServicesPage_Loaded(object sender, RoutedEventArgs e) =>
        await RefreshAsync(includeLogs: _selectedServiceId is not null);

    private void ServicesPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceChangeNotifier.Changed -= ServiceChangeNotifier_Changed;
        _refreshTimer.Stop();
    }

    private async void ServiceChangeNotifier_Changed(object? sender, EventArgs e) =>
        await RefreshAsync(includeLogs: _selectedServiceId is not null);

    private void InstalledServicesGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (InstalledServicesGrid.ItemsPanelRoot is ItemsWrapGrid panel && e.NewSize.Width > 0)
        {
            panel.ItemWidth = Math.Min(456, Math.Max(240, e.NewSize.Width));
        }
    }

    private async void InstalledServicesGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_suppressNextItemClick)
        {
            _suppressNextItemClick = false;
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
            _suppressNextItemClick = true;
            await ToggleServiceAsync(serviceId);
        }
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
        if (_busyServiceId is not null || !_serviceInfo.TryGetValue(serviceId, out var service))
        {
            return;
        }

        _busyServiceId = serviceId;
        RebuildCards();
        ApplyLocalization();

        try
        {
            var status = ShouldStop(service.Status.State)
                ? await _serviceClient.StopAsync(serviceId)
                : await _serviceClient.StartAsync(serviceId);
            if (status is not null)
            {
                _serviceInfo[serviceId] = service with { Status = status };
            }

            await RefreshAsync(includeLogs: _selectedServiceId == serviceId);
        }
        catch (IpcException exception)
        {
            ApplyAgentError(exception);
        }
        finally
        {
            _busyServiceId = null;
            RebuildCards();
            UpdateTimer();
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
            await RefreshLogsAsync(_selectedServiceId);
        }
        catch (IpcException exception)
        {
            ApplyAgentError(exception);
        }
    }

    private async void UninstallMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedService();
        if (selected is null)
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

        _busyServiceId = selected.ServiceId;
        ApplyLocalization();
        try
        {
            await _serviceClient.UninstallAsync(selected.ServiceId, deleteDataCheckBox.IsChecked == true);
            _serviceInfo.Remove(selected.ServiceId);
            _selectedServiceId = null;
            s_lastSelectedServiceId = null;
            ServiceChangeNotifier.NotifyChanged();
            ShowServiceOperation(
                InfoBarSeverity.Success,
                localization.Translate("services.mock.uninstall.success.title"),
                localization.Translate(deleteDataCheckBox.IsChecked == true
                    ? "services.mock.uninstall.success.deleted"
                    : "services.mock.uninstall.success.preserved"));
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
            _busyServiceId = null;
            RebuildCards();
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

    private async Task RefreshLogsAsync(string serviceId)
    {
        var logs = await _serviceClient.GetLogsAsync(serviceId);
        LogsTextBox.Text = string.IsNullOrWhiteSpace(logs)
            ? LocalizationService.Current.Translate("services.mock.logs.empty")
            : logs;
        LogsTextBox.Select(LogsTextBox.Text.Length, 0);
    }

    private void RebuildCards()
    {
        if (InstalledServicesGrid is null)
        {
            return;
        }

        var localization = LocalizationService.Current;
        Services.Clear();
        foreach (var service in _serviceInfo.Values.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var isBusy = string.Equals(_busyServiceId, service.ServiceId, StringComparison.OrdinalIgnoreCase);
            var shouldStop = ShouldStop(service.Status.State);
            Services.Add(new InstalledServiceCard
            {
                ServiceId = service.ServiceId,
                Name = service.Name,
                Message = service.Status.Message,
                StateText = TranslateState(service.Status.State),
                ActionText = localization.Translate(shouldStop ? "services.mock.stop" : "services.mock.start"),
                ActionIcon = shouldStop ? Symbol.Stop : Symbol.Play,
                IsBusy = isBusy,
                IsActionEnabled = _agentAvailable && _busyServiceId is null
            });
        }
    }

    private AgentServiceInfo? GetSelectedService() =>
        _selectedServiceId is not null && _serviceInfo.TryGetValue(_selectedServiceId, out var service)
            ? service
            : null;

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
    }

    private void UpdateTimer()
    {
        var hasActiveServices = _serviceInfo.Values.Any(service => IsActiveState(service.Status.State));
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

    private static bool ShouldStop(string state) =>
        state.Equals("running", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("starting", StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveState(string state) => ShouldStop(state);
}

internal sealed record InstalledServiceCard
{
    public required string ServiceId { get; init; }
    public required string Name { get; init; }
    public required string Message { get; init; }
    public required string StateText { get; init; }
    public required string ActionText { get; init; }
    public Symbol ActionIcon { get; init; }
    public bool IsBusy { get; init; }
    public bool IsActionEnabled { get; init; }
    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconVisibility => IsBusy ? Visibility.Collapsed : Visibility.Visible;
}
