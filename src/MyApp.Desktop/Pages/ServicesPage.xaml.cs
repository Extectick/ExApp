using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyApp.Desktop.Services;
using MyApp.Ipc;

namespace MyApp.Desktop.Pages;

public sealed partial class ServicesPage : Page, ILocalizedPage
{
    private const double ServiceCardMaxWidth = 442;

    private static bool s_lastDetailsOpen;

    private readonly AgentServiceClient _serviceClient = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _isDetailsOpen;
    private bool _isCommandRunning;
    private bool _isRefreshRunning;
    private bool _suppressNextCardTap;
    private bool _agentAvailable = true;
    private bool _isInstalled;
    private string _state = "stopped";
    private string _health = "stopped";
    private string _message = "Mock service is stopped.";
    private string _version = "0.1.0";
    private DateTimeOffset _updatedAt = DateTimeOffset.Now;

    public ServicesPage()
    {
        InitializeComponent();
        Loaded += ServicesPage_Loaded;
        Unloaded += ServicesPage_Unloaded;
        ServiceChangeNotifier.Changed += ServiceChangeNotifier_Changed;
        _isDetailsOpen = s_lastDetailsOpen;
        ApplyLocalization();
        _refreshTimer.Tick += RefreshTimer_Tick;
        _ = RefreshAsync(includeLogs: _isDetailsOpen);
    }

    public void ApplyLocalization()
    {
        var localization = LocalizationService.Current;
        var isStarting = IsStarting;
        var shouldStop = ShouldStopOnToggle;
        var actionText = shouldStop
            ? localization.Translate("services.mock.stop")
            : localization.Translate("services.mock.start");
        var actionIcon = shouldStop ? Symbol.Stop : Symbol.Play;
        var stateText = TranslateState(_state);

        TitleText.Text = localization.Translate("services.title");
        BackButtonText.Text = localization.Translate("services.mock.back");
        BackButton.Visibility = _isDetailsOpen ? Visibility.Visible : Visibility.Collapsed;

        EmptyServicesTitle.Text = localization.Translate("services.empty.title");
        EmptyServicesMessage.Text = localization.Translate("services.empty.message");
        ServiceCardRoot.Visibility = _isInstalled ? Visibility.Visible : Visibility.Collapsed;
        EmptyServicesPanel.Visibility = _isInstalled ? Visibility.Collapsed : Visibility.Visible;

        MockServiceNameText.Text = localization.Translate("services.mock.name");
        DetailsServiceNameText.Text = localization.Translate("services.mock.name");
        MockServiceDescriptionText.Text = localization.Translate("services.mock.description");

        StatusLabelText.Text = localization.Translate("services.mock.statusLabel");
        HealthLabelText.Text = localization.Translate("services.mock.health");
        VersionLabelText.Text = localization.Translate("services.mock.version");
        DetailsStateText.Text = stateText;
        DetailsHealthText.Text = _health;
        DetailsVersionText.Text = _version;
        DetailsServiceMessageText.Text = _message;

        ListToggleButtonText.Text = actionText;
        DetailsToggleButtonText.Text = actionText;
        ListToggleIcon.Symbol = actionIcon;
        DetailsToggleIcon.Symbol = actionIcon;
        ListStartingRing.IsActive = isStarting;
        DetailsStartingRing.IsActive = isStarting;
        ListStartingRing.Visibility = isStarting ? Visibility.Visible : Visibility.Collapsed;
        DetailsStartingRing.Visibility = isStarting ? Visibility.Visible : Visibility.Collapsed;
        ListToggleIcon.Visibility = isStarting ? Visibility.Collapsed : Visibility.Visible;
        DetailsToggleIcon.Visibility = isStarting ? Visibility.Collapsed : Visibility.Visible;

        LogsTitleText.Text = localization.Translate("services.mock.logs");
        UpdatedText.Text = $"{localization.Translate("services.mock.updated")}: {_updatedAt:HH:mm:ss}";
        ListViewRoot.Visibility = _isDetailsOpen ? Visibility.Collapsed : Visibility.Visible;
        DetailsViewRoot.Visibility = _isDetailsOpen ? Visibility.Visible : Visibility.Collapsed;

        var buttonsEnabled = !_isCommandRunning;
        ListToggleButton.IsEnabled = buttonsEnabled && _agentAvailable && _isInstalled;
        DetailsToggleButton.IsEnabled = buttonsEnabled && _agentAvailable && _isInstalled;
        RefreshButton.IsEnabled = !_isRefreshRunning;
        RefreshLogsButton.IsEnabled = !_isRefreshRunning;
        ClearLogsButton.IsEnabled = !_isRefreshRunning;
        ServiceMenuButton.IsEnabled = !_isCommandRunning && _isInstalled;
        UninstallMenuItem.Text = localization.Translate("services.mock.uninstall");
    }

    private async void ServicesPage_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync(includeLogs: _isDetailsOpen);
    }

    private void ServicesPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ServiceChangeNotifier.Changed -= ServiceChangeNotifier_Changed;
        _refreshTimer.Stop();
    }

    private async void ServiceChangeNotifier_Changed(object? sender, EventArgs e)
    {
        await RefreshAsync(includeLogs: _isDetailsOpen);
    }

    private void ServicesScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var availableWidth = Math.Max(0, e.NewSize.Width);
        ServiceCardRoot.Width = Math.Min(ServiceCardMaxWidth, availableWidth);
    }

    private async void ToggleServiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, ListToggleButton))
        {
            _suppressNextCardTap = true;
        }

        if (_isCommandRunning)
        {
            return;
        }

        _isCommandRunning = true;
        ApplyLocalization();

        try
        {
            var status = ShouldStopOnToggle
                ? await _serviceClient.StopAsync()
                : await _serviceClient.StartAsync();
            if (status is not null)
            {
                ApplyStatus(status);
            }

            await RefreshAsync(includeLogs: _isDetailsOpen);
        }
        catch (IpcException exception)
        {
            ApplyAgentError(exception);
        }
        finally
        {
            _isCommandRunning = false;
            UpdateTimer();
            ApplyLocalization();
        }
    }

    private void ToggleButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _suppressNextCardTap = true;
        e.Handled = true;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync(includeLogs: _isDetailsOpen);
    }

    private async void ServiceCardRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_suppressNextCardTap)
        {
            _suppressNextCardTap = false;
            return;
        }

        _isDetailsOpen = true;
        s_lastDetailsOpen = true;
        ApplyLocalization();
        await RefreshAsync(includeLogs: true);
    }

    private void ServiceCardRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (Application.Current.Resources.TryGetValue("SystemFillColorNeutralBackgroundBrush", out var background) &&
            background is Brush backgroundBrush)
        {
            ServiceCardRoot.Background = backgroundBrush;
        }

        if (Application.Current.Resources.TryGetValue("ControlStrokeColorDefaultBrush", out var border) &&
            border is Brush borderBrush)
        {
            ServiceCardRoot.BorderBrush = borderBrush;
        }
    }

    private void ServiceCardRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (Application.Current.Resources.TryGetValue("CardBackgroundFillColorDefaultBrush", out var background) &&
            background is Brush backgroundBrush)
        {
            ServiceCardRoot.Background = backgroundBrush;
        }

        if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out var border) &&
            border is Brush borderBrush)
        {
            ServiceCardRoot.BorderBrush = borderBrush;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _isDetailsOpen = false;
        s_lastDetailsOpen = false;
        ApplyLocalization();
        UpdateTimer();
    }

    private async void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _serviceClient.ClearLogsAsync();
            await RefreshLogsAsync();
        }
        catch (IpcException exception)
        {
            ApplyAgentError(exception);
        }
    }

    private async void UninstallMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var localization = LocalizationService.Current;
        var deleteDataCheckBox = new CheckBox
        {
            Content = localization.Translate("services.mock.uninstall.deleteData"),
            IsChecked = false
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = localization.Translate("services.mock.uninstall.message"),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(deleteDataCheckBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = localization.Translate("services.mock.uninstall.title"),
            Content = content,
            PrimaryButtonText = localization.Translate("services.mock.uninstall.confirm"),
            CloseButtonText = localization.Translate("common.cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _isCommandRunning = true;
        ApplyLocalization();
        try
        {
            await _serviceClient.UninstallAsync(deleteDataCheckBox.IsChecked == true);
            _isInstalled = false;
            _isDetailsOpen = false;
            s_lastDetailsOpen = false;
            ServiceChangeNotifier.NotifyChanged();
            ShowServiceOperation(
                InfoBarSeverity.Success,
                "services.mock.uninstall.success.title",
                deleteDataCheckBox.IsChecked == true
                    ? "services.mock.uninstall.success.deleted"
                    : "services.mock.uninstall.success.preserved");
        }
        catch (IpcException exception)
        {
            ServiceOperationInfoBar.Severity = InfoBarSeverity.Error;
            ServiceOperationInfoBar.Title = localization.Translate("services.mock.uninstall.failed");
            ServiceOperationInfoBar.Message = exception.Message;
            ServiceOperationInfoBar.IsOpen = true;
        }
        finally
        {
            _isCommandRunning = false;
            ApplyLocalization();
        }
    }

    private async void RefreshTimer_Tick(object? sender, object e)
    {
        await RefreshAsync(includeLogs: _isDetailsOpen || IsActiveState(_state));
    }

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
            _isInstalled = serviceList.Any(item => item.ServiceId == "mock-service" && item.Installed);
            if (!_isInstalled)
            {
                _isDetailsOpen = false;
                s_lastDetailsOpen = false;
                _state = "stopped";
                _health = "stopped";
                _message = LocalizationService.Current.Translate("services.empty.message");
                LogsTextBox.Text = string.Empty;
                return;
            }

            var status = await _serviceClient.GetStatusAsync();
            if (status is null)
            {
                _state = "error";
                _health = "error";
                _message = "Agent returned an empty service status.";
                return;
            }

            _agentAvailable = true;
            ApplyStatus(status);
            if (includeLogs)
            {
                await RefreshLogsAsync();
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

    private async Task RefreshLogsAsync()
    {
        var logs = await _serviceClient.GetLogsAsync();
        LogsTextBox.Text = string.IsNullOrWhiteSpace(logs)
            ? LocalizationService.Current.Translate("services.mock.logs.empty")
            : logs;
        LogsTextBox.Select(LogsTextBox.Text.Length, 0);
    }

    private void ApplyStatus(AgentServiceStatus status)
    {
        _state = status.State;
        _health = status.Health;
        _message = status.Message;
        _version = status.Version;
        _updatedAt = status.UpdatedAt.ToLocalTime();
    }

    private void ApplyAgentError(IpcException exception)
    {
        _agentAvailable = exception.Code is not "ipc.timeout" and not "ipc.emptyResponse";
        _state = "error";
        _health = "error";
        _message = exception.Message;
        _updatedAt = DateTimeOffset.Now;
        ApplyLocalization();
    }

    private void ShowServiceOperation(InfoBarSeverity severity, string titleKey, string messageKey)
    {
        var localization = LocalizationService.Current;
        ServiceOperationInfoBar.Severity = severity;
        ServiceOperationInfoBar.Title = localization.Translate(titleKey);
        ServiceOperationInfoBar.Message = localization.Translate(messageKey);
        ServiceOperationInfoBar.IsOpen = true;
    }

    private void UpdateTimer()
    {
        if (_isInstalled && (_isDetailsOpen || IsActiveState(_state)))
        {
            if (!_refreshTimer.IsEnabled)
            {
                _refreshTimer.Start();
            }
        }
        else if (_refreshTimer.IsEnabled)
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

        return string.IsNullOrEmpty(key)
            ? state
            : LocalizationService.Current.Translate(key);
    }

    private bool IsStarting => _state.Equals("starting", StringComparison.OrdinalIgnoreCase);

    private bool ShouldStopOnToggle =>
        _state.Equals("running", StringComparison.OrdinalIgnoreCase) ||
        _state.Equals("starting", StringComparison.OrdinalIgnoreCase);

    private static bool IsActiveState(string state) =>
        state.Equals("running", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("starting", StringComparison.OrdinalIgnoreCase);
}
