using ExApp.Agent;
using ExApp.Packaging;
using ExApp.ServiceRuntime;
using System.Reflection;

using var singleInstance = new Mutex(initiallyOwned: true, @"Local\ExApp.Agent", out var isFirstInstance);
if (!isFirstInstance)
{
    return;
}

var builder = Host.CreateApplicationBuilder(args);
var agentOptions = new AgentOptions();
var agentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
builder.Services.AddSingleton(agentOptions);
builder.Services.AddSingleton(new PackageManager(new PackageManagerOptions
{
    RootDirectory = agentOptions.RootDirectory,
    AppVersion = agentVersion,
    AgentVersion = agentVersion,
    Architecture = "x64"
}));
builder.Services.AddSingleton(new ServiceRuntimeOptions
{
    RootDirectory = agentOptions.RootDirectory,
    MockServiceExecutable = Environment.GetEnvironmentVariable("EXAPP_MOCK_SERVICE_EXE"),
    CommandTimeout = TimeSpan.FromSeconds(8)
});
builder.Services.AddSingleton<ServiceRegistry>();
builder.Services.AddSingleton<ServiceCommandRunner>();
builder.Services.AddSingleton<ServiceProcessMonitor>();
builder.Services.AddSingleton<ServiceSupervisor>();
builder.Services.AddSingleton<ServiceLogRouter>();
builder.Services.AddSingleton<ServiceRuntime>();
builder.Services.AddSingleton<ServiceProcessClient>();
builder.Services.AddSingleton<AgentRequestHandler>();
builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
host.Run();
