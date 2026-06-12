using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ExApp.Ipc;

public sealed class NamedPipeIpcClient(string pipeName)
{
    public async Task<TResponse?> SendAsync<TRequest, TResponse>(
        string command,
        TRequest payload,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(command, IpcJson.ToElement(payload), timeout, cancellationToken);
        if (!response.Success)
        {
            throw new IpcException(
                response.Error?.Code ?? "ipc.unknown",
                response.Error?.Message ?? "Agent returned an unknown error.");
        }

        return response.Result is null
            ? default
            : IpcJson.FromElement<TResponse>(response.Result.Value);
    }

    public async Task<IpcResponse> SendAsync(
        string command,
        JsonElement payload,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(8);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(effectiveTimeout);

        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await pipe.ConnectAsync(timeoutSource.Token);
            var request = new IpcRequest
            {
                Command = command,
                Payload = payload
            };

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, IpcJson.Options));
            var responseLine = await reader.ReadLineAsync(timeoutSource.Token);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                throw new IpcException("ipc.emptyResponse", "Agent returned an empty response.");
            }

            return JsonSerializer.Deserialize<IpcResponse>(responseLine, IpcJson.Options)
                ?? throw new IpcException("ipc.invalidResponse", "Agent returned an invalid response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IpcException("ipc.timeout", $"Agent request timed out after {effectiveTimeout.TotalSeconds:0.#} seconds.");
        }
        catch (TimeoutException)
        {
            throw new IpcException("ipc.timeout", $"Agent connection timed out after {effectiveTimeout.TotalSeconds:0.#} seconds.");
        }
    }
}

public sealed class IpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
