using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ExApp.Desktop.Services;
using ExApp.Ipc;

namespace ExApp.Desktop.Pages;

public sealed partial class DiagnosticsPage : Page, ILocalizedPage
{
    private readonly AgentServiceClient _agentClient = new();
    private AgentDiagnosticsSnapshot? _snapshot;
    private bool _isLoading = true;

    internal ObservableCollection<DiagnosticServiceRow> Services { get; } = [];

    public DiagnosticsPage()
    {
        InitializeComponent();
        Loaded += DiagnosticsPage_Loaded;
        ApplyLocalization();
    }

    public void ApplyLocalization()
    {
        var localization = LocalizationService.Current;
        TitleText.Text = localization.Translate("diagnostics.title");
        SubtitleText.Text = localization.Translate("diagnostics.subtitle");
        ExportButtonText.Text = localization.Translate("diagnostics.export");
        InstalledLabelText.Text = localization.Translate("diagnostics.installed");
        RunningLabelText.Text = localization.Translate("diagnostics.running");
        FailedLabelText.Text = localization.Translate("diagnostics.failed");
        AgentLabelText.Text = localization.Translate("diagnostics.agentLabel");
        VersionLabelText.Text = localization.Translate("diagnostics.version");
        RootLabelText.Text = localization.Translate("diagnostics.root");
        LoadingText.Text = localization.Translate("diagnostics.loading");
        AgentStatusText.Text = localization.Translate(
            _snapshot is null ? "diagnostics.agent.disconnected" : "diagnostics.agent.connected");
        ToolTipService.SetToolTip(RefreshButton, localization.Translate("diagnostics.refresh"));
        AutomationProperties.SetName(RefreshButton, localization.Translate("diagnostics.refresh"));
        UpdateView();
    }

    private async void DiagnosticsPage_Loaded(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_isLoading && _snapshot is not null)
        {
            return;
        }

        _isLoading = true;
        UpdateView();
        try
        {
            _snapshot = await _agentClient.GetDiagnosticsAsync();
            RebuildRows();
        }
        catch (IpcException exception)
        {
            _snapshot = null;
            Services.Clear();
            ShowOperation(InfoBarSeverity.Error, LocalizationService.Current.Translate("diagnostics.loadFailed"), exception.Message);
        }
        finally
        {
            _isLoading = false;
            ApplyLocalization();
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            return;
        }

        ExportButton.IsEnabled = false;
        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            Directory.CreateDirectory(downloads);
            var bundlePath = Path.Combine(downloads, $"ExApp-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            var staging = Path.Combine(Path.GetTempPath(), "ExApp.Diagnostics", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                await File.WriteAllTextAsync(
                    Path.Combine(staging, "snapshot.json"),
                    JsonSerializer.Serialize(_snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

                foreach (var service in _snapshot.Services.Where(service => service.Installed))
                {
                    var logs = await _agentClient.GetLogsAsync(service.ServiceId);
                    await File.WriteAllTextAsync(Path.Combine(staging, $"{service.ServiceId}.log"), logs);
                }

                var appLog = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ExApp",
                    "logs",
                    "app.log");
                if (File.Exists(appLog))
                {
                    File.Copy(appLog, Path.Combine(staging, "app.log"), overwrite: true);
                }

                ZipFile.CreateFromDirectory(staging, bundlePath, CompressionLevel.Fastest, includeBaseDirectory: false);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }

            ShowOperation(InfoBarSeverity.Success, LocalizationService.Current.Translate("diagnostics.exported"), bundlePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or IpcException)
        {
            ShowOperation(InfoBarSeverity.Error, LocalizationService.Current.Translate("diagnostics.exportFailed"), exception.Message);
        }
        finally
        {
            ExportButton.IsEnabled = _snapshot is not null;
        }
    }

    private void RebuildRows()
    {
        Services.Clear();
        if (_snapshot is null)
        {
            return;
        }

        foreach (var service in _snapshot.Services.Where(service => service.Installed))
        {
            Services.Add(new DiagnosticServiceRow(
                service.Name,
                service.Status.Message,
                service.Status.State,
                service.Status.LastError ?? string.Empty,
                service.Runtime?.ProcessId is int pid ? $"PID {pid}" : "-",
                FormatUptime(service.Runtime?.UptimeSeconds)));
        }
    }

    private void UpdateView()
    {
        InstalledValueText.Text = (_snapshot?.InstalledServices ?? 0).ToString();
        RunningValueText.Text = (_snapshot?.RunningServices ?? 0).ToString();
        FailedValueText.Text = (_snapshot?.FailedServices ?? 0).ToString();
        AgentVersionText.Text = _snapshot?.AgentVersion ?? "-";
        RootPathText.Text = _snapshot?.RootDirectory ?? "-";
        LoadingPanel.Visibility = _isLoading ? Visibility.Visible : Visibility.Collapsed;
        ServicesList.Visibility = !_isLoading ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = !_isLoading && _snapshot is not null;
        RefreshButton.IsEnabled = !_isLoading;
    }

    private void ShowOperation(InfoBarSeverity severity, string title, string message)
    {
        OperationInfoBar.Severity = severity;
        OperationInfoBar.Title = title;
        OperationInfoBar.Message = message;
        OperationInfoBar.IsOpen = true;
    }

    private static string FormatUptime(long? seconds)
    {
        if (seconds is null)
        {
            return "-";
        }

        var value = TimeSpan.FromSeconds(seconds.Value);
        return value.TotalDays >= 1
            ? $"{(int)value.TotalDays}d {value:hh\\:mm\\:ss}"
            : value.ToString(@"hh\:mm\:ss");
    }
}

internal sealed record DiagnosticServiceRow(
    string Name,
    string Message,
    string State,
    string LastError,
    string ProcessText,
    string UptimeText);
