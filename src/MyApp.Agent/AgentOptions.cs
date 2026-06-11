namespace MyApp.Agent;

internal sealed record AgentOptions
{
    public const string PipeName = "ExApp.Agent.v1";

    public string RootDirectory { get; init; } =
        Environment.GetEnvironmentVariable("EXAPP_ROOT")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExApp");
}
