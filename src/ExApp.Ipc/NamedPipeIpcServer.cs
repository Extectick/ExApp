using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ExApp.Ipc;

public sealed class NamedPipeIpcServer(
    string pipeName,
    Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                _ = HandleConnectionAsync(pipe, cancellationToken);
            }
            catch
            {
                await pipe.DisposeAsync();
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                throw;
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };

            IpcResponse response;
            try
            {
                var requestLine = await reader.ReadLineAsync(cancellationToken);
                var request = string.IsNullOrWhiteSpace(requestLine)
                    ? null
                    : JsonSerializer.Deserialize<IpcRequest>(requestLine, IpcJson.Options);
                if (request is null || string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.Command))
                {
                    response = Error(string.Empty, "ipc.invalidRequest", "IPC request is invalid.");
                }
                else
                {
                    response = await handler(request, cancellationToken);
                }
            }
            catch (JsonException)
            {
                response = Error(string.Empty, "ipc.invalidJson", "IPC request contains invalid JSON.");
            }
            catch (Exception exception)
            {
                response = Error(string.Empty, "ipc.internalError", exception.Message);
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(response, IpcJson.Options));
        }
    }

    public static IpcResponse Success<T>(string requestId, T result) => new()
    {
        RequestId = requestId,
        Success = true,
        Result = IpcJson.ToElement(result)
    };

    public static IpcResponse Error(string requestId, string code, string message) => new()
    {
        RequestId = requestId,
        Success = false,
        Error = new IpcError { Code = code, Message = message }
    };
}
