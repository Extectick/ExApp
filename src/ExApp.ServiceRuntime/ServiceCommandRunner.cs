using System.Diagnostics;

namespace ExApp.ServiceRuntime;

public sealed class ServiceCommandRunner(ServiceRuntimeOptions options)
{
    public Task<ServiceCommandResult> ExecuteAsync(
        ServiceDescriptor service,
        string command,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(service, command, null, cancellationToken);

    public async Task<ServiceCommandResult> ExecuteAsync(
        ServiceDescriptor service,
        string command,
        IReadOnlyList<string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var executable = service.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new ServiceRuntimeException("service.executableMissing", $"Service executable for '{service.ServiceId}' was not found.");
        }

        Directory.CreateDirectory(service.RuntimeDirectory);
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        startInfo.ArgumentList.Add(command);
        foreach (var argument in arguments ?? [])
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--state");
        startInfo.ArgumentList.Add(service.RuntimeDirectory);

        using var process = Process.Start(startInfo)
            ?? throw new ServiceRuntimeException("service.startFailed", $"Failed to execute service command '{command}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var timeout = options.CommandTimeout == default
            ? TimeSpan.FromSeconds(8)
            : options.CommandTimeout;

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            TryKill(process);
            throw new ServiceRuntimeException("service.timeout", $"Service command '{command}' timed out.");
        }

        return new ServiceCommandResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed record ServiceCommandResult(int ExitCode, string StandardOutput, string StandardError);
