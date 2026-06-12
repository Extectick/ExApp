namespace ExApp.Desktop.Services;

internal enum ServiceOperationKind
{
    Install,
    Start,
    Stop,
    Restart,
    Uninstall,
    Update
}

internal sealed class ServiceOperationCoordinator
{
    private readonly Dictionary<string, ServiceOperationKind> _operations = new(StringComparer.OrdinalIgnoreCase);

    public static ServiceOperationCoordinator Current { get; } = new();

    public event EventHandler? Changed;

    public bool IsActive(string serviceId) => GetKind(serviceId) is not null;

    public ServiceOperationKind? GetKind(string serviceId)
    {
        lock (_operations)
        {
            return _operations.TryGetValue(serviceId, out var kind) ? kind : null;
        }
    }

    public async Task<bool> RunAsync(
        string serviceId,
        ServiceOperationKind kind,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentNullException.ThrowIfNull(operation);

        lock (_operations)
        {
            if (_operations.ContainsKey(serviceId))
            {
                return false;
            }

            _operations[serviceId] = kind;
        }

        OnChanged();
        try
        {
            await operation(cancellationToken);
            return true;
        }
        finally
        {
            lock (_operations)
            {
                _operations.Remove(serviceId);
            }

            OnChanged();
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
