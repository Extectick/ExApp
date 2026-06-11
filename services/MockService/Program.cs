using System.Diagnostics;
using System.Text.Json;

const string ServiceId = "mock-service";
const string Version = "0.1.0";
const int MaxLogLines = 300;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
var stateRoot = GetOption(args, "--state")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExApp", "services", ServiceId);

Directory.CreateDirectory(stateRoot);

var statePath = Path.Combine(stateRoot, "state.json");
var logPath = Path.Combine(stateRoot, "heartbeat.log");
var pidPath = Path.Combine(stateRoot, "service.pid");

return command switch
{
    "start" => StartService(args, stateRoot, statePath, pidPath, logPath),
    "run" => await RunServiceAsync(statePath, pidPath, logPath),
    "stop" => StopService(statePath, pidPath, logPath),
    "status" => WriteStatus(statePath, pidPath),
    "logs" => WriteLogs(logPath),
    "clear-logs" => ClearLogs(logPath),
    _ => WriteError($"Unknown command '{command}'. Supported: start, run, stop, status, logs, clear-logs.")
};

static int StartService(string[] args, string stateRoot, string statePath, string pidPath, string logPath)
{
    var existing = ReadPid(pidPath);
    if (existing is not null && IsProcessRunning(existing.Value))
    {
        WriteJson(ReadState(statePath) with { State = "running", Health = "ok", Message = "Mock service is already running." });
        return 0;
    }

    var executable = Environment.ProcessPath;
    if (string.IsNullOrWhiteSpace(executable))
    {
        return WriteError("Cannot resolve mock service executable path.");
    }

    Directory.CreateDirectory(stateRoot);
    WriteState(statePath, "starting", "starting", "Mock service is starting.", null);

    var startInfo = new ProcessStartInfo(executable)
    {
        UseShellExecute = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
        Arguments = $"run --state \"{stateRoot}\""
    };

    var process = Process.Start(startInfo);
    if (process is null)
    {
        WriteState(statePath, "error", "error", "Failed to start mock service process.", "Process.Start returned null.");
        return WriteError("Failed to start mock service process.");
    }

    File.WriteAllText(pidPath, process.Id.ToString());
    AppendLog(logPath, $"Started mock service process {process.Id}.");
    WriteJson(ReadState(statePath));
    return 0;
}

static async Task<int> RunServiceAsync(string statePath, string pidPath, string logPath)
{
    File.WriteAllText(pidPath, Environment.ProcessId.ToString());
    WriteState(statePath, "starting", "starting", "Mock service is starting.", null);
    AppendLog(logPath, "Startup sequence started.");

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    try
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellation.Token);
        WriteState(statePath, "running", "ok", "Mock service is running.", null);
        AppendLog(logPath, "Heartbeat loop started.");

        while (!cancellation.IsCancellationRequested)
        {
            WriteState(statePath, "running", "ok", "Mock service heartbeat.", null);
            AppendLog(logPath, "heartbeat");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
        }
    }
    catch (OperationCanceledException)
    {
        // Normal stop path.
    }

    WriteState(statePath, "stopped", "stopped", "Mock service stopped.", null);
    AppendLog(logPath, "Heartbeat loop stopped.");
    return 0;
}

static int StopService(string statePath, string pidPath, string logPath)
{
    var pid = ReadPid(pidPath);
    if (pid is null)
    {
        WriteState(statePath, "stopped", "stopped", "Mock service is not running.", null);
        AppendLog(logPath, "Stop requested, but mock service was not running.");
        WriteJson(ReadState(statePath));
        return 0;
    }

    AppendLog(logPath, $"Stop requested for mock service process {pid.Value}.");
    try
    {
        var process = Process.GetProcessById(pid.Value);
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
        AppendLog(logPath, $"Stopped mock service process {pid.Value}.");
    }
    catch (ArgumentException)
    {
        AppendLog(logPath, $"Mock service process {pid.Value} was already stopped.");
    }

    File.Delete(pidPath);
    WriteState(statePath, "stopped", "stopped", "Mock service stopped.", null);
    WriteJson(ReadState(statePath));
    return 0;
}

static int WriteStatus(string statePath, string pidPath)
{
    var state = ReadState(statePath);
    var pid = ReadPid(pidPath);
    if (pid is not null && IsProcessRunning(pid.Value))
    {
        state = state.State.Equals("starting", StringComparison.OrdinalIgnoreCase)
            ? state with { State = "starting", Health = "starting", Message = "Mock service is starting." }
            : state with { State = "running", Health = "ok", Message = "Mock service is running." };
    }
    else if (state.State is "running" or "starting")
    {
        state = state with { State = "crashed", Health = "error", Message = "Mock service process is not running.", LastError = "Process exited unexpectedly." };
    }

    WriteJson(state);
    return 0;
}

static int WriteLogs(string logPath)
{
    if (!File.Exists(logPath))
    {
        return 0;
    }

    foreach (var line in File.ReadLines(logPath).TakeLast(50))
    {
        Console.WriteLine(line);
    }

    return 0;
}

static int ClearLogs(string logPath)
{
    File.WriteAllText(logPath, string.Empty);
    return 0;
}

static string? GetOption(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int? ReadPid(string pidPath)
{
    if (!File.Exists(pidPath) || !int.TryParse(File.ReadAllText(pidPath).Trim(), out var pid))
    {
        return null;
    }

    return pid;
}

static bool IsProcessRunning(int pid)
{
    try
    {
        _ = Process.GetProcessById(pid);
        return true;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

static ServiceStatus ReadState(string statePath)
{
    if (!File.Exists(statePath))
    {
        return new ServiceStatus(ServiceId, Version, "stopped", "stopped", "Mock service is stopped.", null, DateTimeOffset.UtcNow);
    }

    return JsonSerializer.Deserialize<ServiceStatus>(File.ReadAllText(statePath))
        ?? new ServiceStatus(ServiceId, Version, "stopped", "stopped", "Mock service is stopped.", null, DateTimeOffset.UtcNow);
}

static void WriteState(string statePath, string state, string health, string message, string? lastError)
{
    WriteJson(new ServiceStatus(ServiceId, Version, state, health, message, lastError, DateTimeOffset.UtcNow), statePath);
}

static void WriteJson(ServiceStatus status, string? path = null)
{
    var json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
    if (path is null)
    {
        Console.WriteLine(json);
    }
    else
    {
        File.WriteAllText(path, json);
    }
}

static int WriteError(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static void AppendLog(string logPath, string message)
{
    var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
    for (var attempt = 0; attempt < 5; attempt++)
    {
        try
        {
            File.AppendAllText(logPath, line);
            TrimLog(logPath);
            return;
        }
        catch (IOException) when (attempt < 4)
        {
            Thread.Sleep(50);
        }
    }
}

static void TrimLog(string logPath)
{
    try
    {
        var lines = File.ReadLines(logPath).TakeLast(MaxLogLines).ToArray();
        if (lines.Length >= MaxLogLines)
        {
            File.WriteAllLines(logPath, lines);
        }
    }
    catch (IOException)
    {
        // Log trimming is best-effort; losing trimming is better than crashing the service.
    }
}

internal sealed record ServiceStatus(
    string ServiceId,
    string Version,
    string State,
    string Health,
    string Message,
    string? LastError,
    DateTimeOffset UpdatedAt);
