using Microsoft.UI.Xaml.Controls;
using MyApp.Desktop.Services;

namespace MyApp.Desktop.Pages;

public sealed partial class DiagnosticsPage : Page, ILocalizedPage
{
    private readonly AgentServiceClient _agentClient = new();
    private bool _agentConnected;

    public DiagnosticsPage()
    {
        InitializeComponent();
        ApplyLocalization();
        _ = RefreshAgentStatusAsync();
    }

    public void ApplyLocalization()
    {
        var localization = LocalizationService.Current;
        TitleText.Text = localization.Translate("diagnostics.title");
        ReadyInfo.Title = localization.Translate("diagnostics.ready.title");
        ReadyInfo.Message = localization.Translate("diagnostics.ready.message");
        AppVersionText.Text = localization.Translate("diagnostics.appVersion");
        AgentStatusText.Text = localization.Translate(_agentConnected ? "diagnostics.agent.connected" : "diagnostics.agent.disconnected");
    }

    private async Task RefreshAgentStatusAsync()
    {
        _agentConnected = await _agentClient.PingAsync();
        ApplyLocalization();
    }
}
