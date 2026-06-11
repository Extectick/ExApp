namespace MyApp.Ipc.Tests;

public sealed class NamedPipeIpcTests
{
    [Fact]
    public async Task SendAsync_RoundTripsTypedPayload()
    {
        var pipeName = $"ExApp.Tests.{Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource();
        var server = new NamedPipeIpcServer(pipeName, (request, _) =>
        {
            var payload = IpcJson.FromElement<TestPayload>(request.Payload);
            return Task.FromResult(NamedPipeIpcServer.Success(
                request.RequestId,
                new TestPayload($"{payload!.Value}-response")));
        });
        var serverTask = server.RunAsync(cancellation.Token);
        var client = new NamedPipeIpcClient(pipeName);

        var response = await client.SendAsync<TestPayload, TestPayload>(
            "test.echo",
            new TestPayload("request"));

        Assert.Equal("request-response", response?.Value);
        cancellation.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task SendAsync_ErrorResponse_ThrowsTypedException()
    {
        var pipeName = $"ExApp.Tests.{Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource();
        var server = new NamedPipeIpcServer(pipeName, (request, _) =>
            Task.FromResult(NamedPipeIpcServer.Error(request.RequestId, "test.failure", "Expected failure.")));
        var serverTask = server.RunAsync(cancellation.Token);
        var client = new NamedPipeIpcClient(pipeName);

        var exception = await Assert.ThrowsAsync<IpcException>(
            () => client.SendAsync<object, object>("test.fail", new { }));

        Assert.Equal("test.failure", exception.Code);
        cancellation.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task SendAsync_MissingServer_TimesOut()
    {
        var client = new NamedPipeIpcClient($"ExApp.Tests.{Guid.NewGuid():N}");

        var exception = await Assert.ThrowsAsync<IpcException>(
            () => client.SendAsync<object, object>("test.timeout", new { }, TimeSpan.FromMilliseconds(150)));

        Assert.Equal("ipc.timeout", exception.Code);
    }

    private sealed record TestPayload(string Value);
}
