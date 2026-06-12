using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

const string ServiceId = "vpn-client";
const string Version = "0.1.0";
const int MaxLogLines = 500;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
var stateRoot = GetOption(args, "--state")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExApp", "services", ServiceId);

Directory.CreateDirectory(stateRoot);
var statePath = Path.Combine(stateRoot, "state.json");
var logPath = Path.Combine(stateRoot, "service.log");
var pidPath = Path.Combine(stateRoot, "service.pid");
var corePidPath = Path.Combine(stateRoot, "core.pid");

return command switch
{
    "start" => StartService(stateRoot, statePath, pidPath, logPath),
    "run" => await RunServiceAsync(stateRoot, statePath, pidPath, corePidPath, logPath),
    "stop" => StopService(statePath, pidPath, corePidPath, logPath),
    "status" => WriteStatus(statePath, pidPath),
    "logs" => WriteLogs(logPath),
    "clear-logs" => ClearLogs(logPath),
    "config-check" => await CheckConfigurationAsync(stateRoot, logPath),
    _ => WriteError($"Unknown command '{command}'.")
};

static int StartService(string stateRoot, string statePath, string pidPath, string logPath)
{
    var existingPid = ReadPid(pidPath);
    if (existingPid is not null && IsProcessRunning(existingPid.Value))
    {
        WriteJson(ReadState(statePath));
        return 0;
    }

    var executable = Environment.ProcessPath;
    if (string.IsNullOrWhiteSpace(executable))
    {
        return WriteError("Cannot resolve VPN service executable path.");
    }

    WriteState(statePath, "starting", "starting", "VPN service is starting.", null);
    Process? process;
    try
    {
        process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            Arguments = $"run --state \"{stateRoot}\""
        });
    }
    catch (System.ComponentModel.Win32Exception exception)
    {
        WriteState(
            statePath,
            "needsPermission",
            "blocked",
            "Administrator permission was not granted.",
            Redact(exception.Message));
        AppendLog(logPath, "Start cancelled: administrator permission was not granted.");
        WriteJson(ReadState(statePath));
        return 5;
    }

    if (process is null)
    {
        WriteState(statePath, "error", "error", "Failed to start VPN service.", "Process.Start returned null.");
        return WriteError("Failed to start VPN service process.");
    }

    File.WriteAllText(pidPath, process.Id.ToString());
    AppendLog(logPath, $"Started VPN service process {process.Id}.");
    WriteJson(ReadState(statePath));
    return 0;
}

static async Task<int> RunServiceAsync(
    string stateRoot,
    string statePath,
    string pidPath,
    string corePidPath,
    string logPath)
{
    File.WriteAllText(pidPath, Environment.ProcessId.ToString());
    if (!IsAdministrator())
    {
        WriteState(statePath, "needsPermission", "blocked", "Administrator permission is required.", null);
        File.Delete(pidPath);
        return 5;
    }

    var corePath = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
    var configPath = Path.Combine(stateRoot, "config.json");

    if (!File.Exists(corePath))
    {
        return Fail(statePath, logPath, "sing-box.exe is missing from the service package.");
    }

    if (!File.Exists(configPath))
    {
        return Fail(statePath, logPath, "VPN configuration is missing.");
    }

    WriteState(statePath, "starting", "starting", "Starting VPN core.", null);
    AppendLog(logPath, "Starting sing-box with the configured profile.");

    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(corePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = stateRoot,
            Arguments = $"run -c \"{configPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true
        },
        EnableRaisingEvents = true
    };

    process.OutputDataReceived += (_, eventArgs) => AppendCoreLog(logPath, eventArgs.Data);
    process.ErrorDataReceived += (_, eventArgs) => AppendCoreLog(logPath, eventArgs.Data);

    try
    {
        if (!process.Start())
        {
            return Fail(statePath, logPath, "sing-box failed to start.");
        }

        File.WriteAllText(corePidPath, process.Id.ToString());
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await Task.Delay(TimeSpan.FromSeconds(1));

        if (process.HasExited)
        {
            return Fail(statePath, logPath, $"sing-box exited with code {process.ExitCode}.");
        }

        WriteState(statePath, "running", "ok", "VPN core is running.", null);
        AppendLog(logPath, $"sing-box process {process.Id} is running.");
        await process.WaitForExitAsync();
        return Fail(statePath, logPath, $"sing-box exited with code {process.ExitCode}.");
    }
    catch (Exception exception) when (
        exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        return Fail(statePath, logPath, "VPN core failed.", Redact(exception.Message));
    }
    finally
    {
        File.Delete(corePidPath);
        File.Delete(pidPath);
    }
}

static int StopService(string statePath, string pidPath, string corePidPath, string logPath)
{
    WriteState(statePath, "stopping", "stopping", "VPN service is stopping.", null);
    KillProcess(ReadPid(corePidPath), logPath, "sing-box");
    KillProcess(ReadPid(pidPath), logPath, "VPN service");
    File.Delete(corePidPath);
    File.Delete(pidPath);
    WriteState(statePath, "stopped", "stopped", "VPN service stopped.", null);
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
            ? state
            : state with { State = "running", Health = "ok", Message = "VPN core is running." };
    }
    else if (state.State is "running" or "starting" or "stopping")
    {
        state = state with
        {
            State = "crashed",
            Health = "error",
            Message = "VPN service process is not running.",
            LastError = "Process exited unexpectedly.",
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    WriteJson(state);
    return 0;
}

static async Task<int> CheckConfigurationAsync(string stateRoot, string logPath)
{
    var corePath = Path.Combine(AppContext.BaseDirectory, "sing-box.exe");
    var configPath = Path.Combine(stateRoot, "config.json");
    if (!File.Exists(corePath) || !File.Exists(configPath))
    {
        return WriteError("sing-box.exe or config.json is missing.");
    }

    using var process = Process.Start(new ProcessStartInfo(corePath)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        Arguments = $"check -c \"{configPath}\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true
    });
    if (process is null)
    {
        return WriteError("Failed to start sing-box config validation.");
    }

    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    AppendCoreLog(logPath, output);
    AppendCoreLog(logPath, error);
    return process.ExitCode;
}

static void KillProcess(int? pid, string logPath, string processName)
{
    if (pid is null)
    {
        return;
    }

    try
    {
        var process = Process.GetProcessById(pid.Value);
        if (process.Id != Environment.ProcessId)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        AppendLog(logPath, $"Stopped {processName} process {pid.Value}.");
    }
    catch (ArgumentException)
    {
        AppendLog(logPath, $"{processName} process {pid.Value} was already stopped.");
    }
}

static int Fail(string statePath, string logPath, string message, string? error = null)
{
    WriteState(statePath, "error", "error", message, error ?? message);
    AppendLog(logPath, error ?? message);
    return 1;
}

static string? GetOption(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int? ReadPid(string path) =>
    File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var pid) ? pid : null;

static bool IsProcessRunning(int pid)
{
    try
    {
        return !Process.GetProcessById(pid).HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

static ServiceStatus ReadState(string statePath)
{
    if (!File.Exists(statePath))
    {
        return new ServiceStatus(ServiceId, Version, "stopped", "stopped", "VPN service is stopped.", null, DateTimeOffset.UtcNow);
    }

    return JsonSerializer.Deserialize<ServiceStatus>(File.ReadAllText(statePath))
        ?? new ServiceStatus(ServiceId, Version, "stopped", "stopped", "VPN service is stopped.", null, DateTimeOffset.UtcNow);
}

static void WriteState(string statePath, string state, string health, string message, string? lastError) =>
    WriteJson(new ServiceStatus(ServiceId, Version, state, health, message, lastError, DateTimeOffset.UtcNow), statePath);

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

static int WriteLogs(string logPath)
{
    if (File.Exists(logPath))
    {
        foreach (var line in File.ReadLines(logPath).TakeLast(100))
        {
            Console.WriteLine(line);
        }
    }
    return 0;
}

static int ClearLogs(string logPath)
{
    File.WriteAllText(logPath, string.Empty);
    return 0;
}

static int WriteError(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static void AppendCoreLog(string logPath, string? message)
{
    if (!string.IsNullOrWhiteSpace(message))
    {
        AppendLog(logPath, message);
    }
}

static void AppendLog(string logPath, string message)
{
    File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {Redact(message)}{Environment.NewLine}");
    var lines = File.ReadLines(logPath).TakeLast(MaxLogLines).ToArray();
    if (lines.Length >= MaxLogLines)
    {
        File.WriteAllLines(logPath, lines);
    }
}

static string Redact(string value)
{
    value = Regex.Replace(value, @"https?://[^\s]+", match =>
        Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}/***" : "***",
        RegexOptions.IgnoreCase);
    value = Regex.Replace(
        value,
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        "***",
        RegexOptions.IgnoreCase);
    return Regex.Replace(value, @"(?i)(password|token|private[_-]?key)\s*[:=]\s*\S+", "$1=***");
}

internal sealed record ServiceStatus(
    string ServiceId,
    string Version,
    string State,
    string Health,
    string Message,
    string? LastError,
    DateTimeOffset UpdatedAt);
