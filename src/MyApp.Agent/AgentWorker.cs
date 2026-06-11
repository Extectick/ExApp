using MyApp.Ipc;

namespace MyApp.Agent;

internal sealed class AgentWorker(
    AgentRequestHandler handler,
    ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ExApp Agent starting on pipe {PipeName}.", AgentOptions.PipeName);
        var server = new NamedPipeIpcServer(AgentOptions.PipeName, handler.HandleAsync);
        await server.RunAsync(stoppingToken);
    }
}
