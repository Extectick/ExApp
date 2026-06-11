namespace MyApp.ServiceRuntime;

public sealed class ServiceRuntimeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
