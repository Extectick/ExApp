using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
var subscriptionPath = Path.Combine(stateRoot, "subscription.dpapi");
var nodesPath = Path.Combine(stateRoot, "nodes.json");
var selectedNodePath = Path.Combine(stateRoot, "selected-node.txt");
var configPath = Path.Combine(stateRoot, "config.json");

return command switch
{
    "start" => StartService(stateRoot, statePath, pidPath, logPath),
    "run" => await RunServiceAsync(stateRoot, statePath, pidPath, corePidPath, logPath),
    "stop" => StopService(statePath, pidPath, corePidPath, logPath),
    "status" => WriteStatus(statePath, pidPath),
    "logs" => WriteLogs(logPath),
    "clear-logs" => ClearLogs(logPath),
    "config-check" or "vpn.config.validate" => await CheckConfigurationAsync(stateRoot, logPath),
    "vpn.subscription.add" => AddSubscription(args, subscriptionPath, logPath),
    "vpn.subscription.clear" => ClearSubscription(subscriptionPath, nodesPath, selectedNodePath, configPath, logPath),
    "vpn.subscription.refresh" => await RefreshSubscriptionAsync(subscriptionPath, nodesPath, selectedNodePath, configPath, logPath),
    "vpn.nodes.list" => ListNodes(subscriptionPath, nodesPath, selectedNodePath),
    "vpn.node.select" => SelectNode(args, nodesPath, selectedNodePath, configPath, logPath),
    "vpn.config.generate" => GenerateConfig(nodesPath, selectedNodePath, configPath, logPath),
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
    else if (state.State is "stopped" or "notConfigured" or "ready")
    {
        state = ReadConfiguredState(Path.GetDirectoryName(statePath)!);
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

static int AddSubscription(string[] args, string subscriptionPath, string logPath)
{
    var url = GetOption(args, "--url");
    if (string.IsNullOrWhiteSpace(url))
    {
        return WriteError("Subscription URL is required. Use --url <url>.");
    }

    if (!Uri.TryCreate(url, UriKind.Absolute, out _) && !File.Exists(url))
    {
        return WriteError("Subscription URL must be an absolute URL or an existing local file path.");
    }

    WriteProtectedText(subscriptionPath, url);
    AppendLog(logPath, $"Subscription saved: {Redact(url)}");
    WriteJsonValue(new VpnCommandResult(true, "Subscription saved.", new { configured = true }));
    return 0;
}

static int ClearSubscription(string subscriptionPath, string nodesPath, string selectedNodePath, string configPath, string logPath)
{
    TryDeleteFile(subscriptionPath);
    TryDeleteFile(nodesPath);
    TryDeleteFile(selectedNodePath);
    TryDeleteFile(configPath);
    AppendLog(logPath, "Subscription and generated VPN configuration were cleared.");
    WriteJsonValue(new VpnCommandResult(true, "Subscription cleared.", new { configured = false }));
    return 0;
}

static async Task<int> RefreshSubscriptionAsync(
    string subscriptionPath,
    string nodesPath,
    string selectedNodePath,
    string configPath,
    string logPath)
{
    var url = ReadProtectedText(subscriptionPath);
    if (string.IsNullOrWhiteSpace(url))
    {
        return WriteError("Subscription is not configured.");
    }

    string content;
    try
    {
        content = await ReadSubscriptionContentAsync(url);
    }
    catch (Exception exception) when (exception is IOException or HttpRequestException or TaskCanceledException or UriFormatException)
    {
        AppendLog(logPath, $"Subscription refresh failed: {Redact(exception.Message)}");
        return WriteError($"Subscription refresh failed: {Redact(exception.Message)}");
    }

    VpnSubscription subscription;
    try
    {
        subscription = ParseSubscription(content);
    }
    catch (Exception exception) when (exception is JsonException or InvalidOperationException)
    {
        AppendLog(logPath, $"Subscription parse failed: {Redact(exception.Message)}");
        return WriteError($"Subscription parse failed: {Redact(exception.Message)}");
    }

    Directory.CreateDirectory(Path.GetDirectoryName(nodesPath)!);
    File.WriteAllText(nodesPath, JsonSerializer.Serialize(subscription, CreateJsonOptions(writeIndented: true)));
    var selectedNodeId = File.Exists(selectedNodePath) ? File.ReadAllText(selectedNodePath).Trim() : null;
    if (string.IsNullOrWhiteSpace(selectedNodeId) ||
        !subscription.Nodes.Any(node => node.Id.Equals(selectedNodeId, StringComparison.OrdinalIgnoreCase)))
    {
        selectedNodeId = subscription.Nodes[0].Id;
        File.WriteAllText(selectedNodePath, selectedNodeId);
    }

    GenerateConfig(nodesPath, selectedNodePath, configPath, logPath, emitOutput: false);
    AppendLog(logPath, $"Subscription refreshed. Nodes: {subscription.Nodes.Count}.");
    WriteJsonValue(new VpnCommandResult(
        true,
        "Subscription refreshed.",
        new
        {
            nodes = subscription.Nodes.Count,
            selectedNodeId
        }));
    return 0;
}

static int ListNodes(string subscriptionPath, string nodesPath, string selectedNodePath)
{
    var subscription = ReadSubscription(nodesPath);
    var selectedNodeId = File.Exists(selectedNodePath) ? File.ReadAllText(selectedNodePath).Trim() : null;
    WriteJsonValue(new
    {
        serviceId = ServiceId,
        configured = File.Exists(subscriptionPath),
        selectedNodeId,
        nodes = subscription?.Nodes.Select(node => new
        {
            node.Id,
            node.Name,
            node.Location,
            type = TryReadString(node.Outbound, "type"),
            server = TryReadString(node.Outbound, "server")
        }).ToArray() ?? []
    });
    return 0;
}

static int SelectNode(string[] args, string nodesPath, string selectedNodePath, string configPath, string logPath)
{
    var nodeId = GetOption(args, "--id");
    if (string.IsNullOrWhiteSpace(nodeId))
    {
        return WriteError("Node id is required. Use --id <node-id>.");
    }

    var subscription = ReadSubscription(nodesPath);
    if (subscription is null)
    {
        return WriteError("Subscription nodes are not loaded. Refresh subscription first.");
    }

    var selected = subscription.Nodes.FirstOrDefault(node => node.Id.Equals(nodeId, StringComparison.OrdinalIgnoreCase));
    if (selected is null)
    {
        return WriteError($"Node '{nodeId}' was not found.");
    }

    File.WriteAllText(selectedNodePath, selected.Id);
    GenerateConfig(nodesPath, selectedNodePath, configPath, logPath, emitOutput: false);
    AppendLog(logPath, $"Selected VPN node: {selected.Name}.");
    WriteJsonValue(new VpnCommandResult(true, "Node selected.", new { selected.Id, selected.Name }));
    return 0;
}

static int GenerateConfig(
    string nodesPath,
    string selectedNodePath,
    string configPath,
    string logPath,
    bool emitOutput = true)
{
    var subscription = ReadSubscription(nodesPath);
    if (subscription is null)
    {
        return WriteError("Subscription nodes are not loaded. Refresh subscription first.");
    }

    var selectedNodeId = File.Exists(selectedNodePath) ? File.ReadAllText(selectedNodePath).Trim() : null;
    var selected = subscription.Nodes.FirstOrDefault(node => node.Id.Equals(selectedNodeId, StringComparison.OrdinalIgnoreCase))
        ?? subscription.Nodes.First();
    File.WriteAllText(selectedNodePath, selected.Id);

    var outbound = JsonNode.Parse(selected.Outbound.GetRawText())?.AsObject()
        ?? throw new InvalidOperationException("Selected node outbound is invalid.");
    outbound["tag"] = "proxy";

    var config = new JsonObject
    {
        ["log"] = new JsonObject
        {
            ["level"] = "info",
            ["timestamp"] = true
        },
        ["inbounds"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = "ExAppTun",
                ["address"] = new JsonArray("172.19.0.1/30"),
                ["auto_route"] = true,
                ["strict_route"] = false,
                ["stack"] = "system"
            }
        },
        ["outbounds"] = new JsonArray
        {
            outbound,
            new JsonObject
            {
                ["type"] = "direct",
                ["tag"] = "direct"
            },
            new JsonObject
            {
                ["type"] = "block",
                ["tag"] = "block"
            }
        },
        ["route"] = new JsonObject
        {
            ["auto_detect_interface"] = true,
            ["final"] = "proxy"
        }
    };

    Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
    File.WriteAllText(configPath, config.ToJsonString(CreateJsonOptions(writeIndented: true)));
    AppendLog(logPath, $"Generated sing-box config for node: {selected.Name}.");
    if (emitOutput)
    {
        WriteJsonValue(new VpnCommandResult(true, "Config generated.", new { selected.Id, selected.Name }));
    }

    return 0;
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

static async Task<string> ReadSubscriptionContentAsync(string url)
{
    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
        if (uri.IsFile)
        {
            return await File.ReadAllTextAsync(uri.LocalPath);
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            return await client.GetStringAsync(uri);
        }
    }

    return await File.ReadAllTextAsync(url);
}

static VpnSubscription ParseSubscription(string json)
{
    var subscription = JsonSerializer.Deserialize<VpnSubscription>(json, CreateJsonOptions())
        ?? throw new InvalidOperationException("Subscription is empty.");
    if (subscription.Nodes.Count == 0)
    {
        throw new InvalidOperationException("Subscription does not contain nodes.");
    }

    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var node in subscription.Nodes)
    {
        if (string.IsNullOrWhiteSpace(node.Id) ||
            !Regex.IsMatch(node.Id, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$"))
        {
            throw new InvalidOperationException("Subscription contains a node with invalid id.");
        }

        if (!ids.Add(node.Id))
        {
            throw new InvalidOperationException($"Subscription contains duplicate node id '{node.Id}'.");
        }

        if (string.IsNullOrWhiteSpace(node.Name))
        {
            throw new InvalidOperationException($"Subscription node '{node.Id}' has no name.");
        }

        if (node.Outbound.ValueKind != JsonValueKind.Object ||
            string.IsNullOrWhiteSpace(TryReadString(node.Outbound, "type")) ||
            string.IsNullOrWhiteSpace(TryReadString(node.Outbound, "server")))
        {
            throw new InvalidOperationException($"Subscription node '{node.Id}' has invalid sing-box outbound.");
        }
    }

    return subscription;
}

static VpnSubscription? ReadSubscription(string nodesPath)
{
    if (!File.Exists(nodesPath))
    {
        return null;
    }

    return JsonSerializer.Deserialize<VpnSubscription>(File.ReadAllText(nodesPath), CreateJsonOptions());
}

static string? TryReadString(JsonElement element, string propertyName) =>
    element.ValueKind == JsonValueKind.Object &&
    element.TryGetProperty(propertyName, out var property) &&
    property.ValueKind == JsonValueKind.String
        ? property.GetString()
        : null;

static JsonSerializerOptions CreateJsonOptions(bool writeIndented = false) =>
    new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = writeIndented
    };

static void WriteProtectedText(string path, string value)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var bytes = Encoding.UTF8.GetBytes(value);
    var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
    File.WriteAllText(path, Convert.ToBase64String(protectedBytes));
}

static string? ReadProtectedText(string path)
{
    if (!File.Exists(path))
    {
        return null;
    }

    var protectedBytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
    var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
    return Encoding.UTF8.GetString(bytes);
}

static void TryDeleteFile(string path)
{
    if (File.Exists(path))
    {
        File.Delete(path);
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
        return ReadConfiguredState(Path.GetDirectoryName(statePath)!);
    }

    return JsonSerializer.Deserialize<ServiceStatus>(File.ReadAllText(statePath))
        ?? ReadConfiguredState(Path.GetDirectoryName(statePath)!);
}

static ServiceStatus ReadConfiguredState(string stateRoot)
{
    var subscriptionPath = Path.Combine(stateRoot, "subscription.dpapi");
    var nodesPath = Path.Combine(stateRoot, "nodes.json");
    var selectedNodePath = Path.Combine(stateRoot, "selected-node.txt");
    var configPath = Path.Combine(stateRoot, "config.json");
    if (!File.Exists(subscriptionPath))
    {
        return new ServiceStatus(ServiceId, Version, "notConfigured", "blocked", "VPN subscription is not configured.", null, DateTimeOffset.UtcNow);
    }

    if (!File.Exists(nodesPath))
    {
        return new ServiceStatus(ServiceId, Version, "notConfigured", "blocked", "VPN subscription was not refreshed.", null, DateTimeOffset.UtcNow);
    }

    if (!File.Exists(selectedNodePath) || !File.Exists(configPath))
    {
        return new ServiceStatus(ServiceId, Version, "ready", "warning", "VPN node is not selected or config is not generated.", null, DateTimeOffset.UtcNow);
    }

    return new ServiceStatus(ServiceId, Version, "stopped", "ready", "VPN service is ready.", null, DateTimeOffset.UtcNow);
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

static void WriteJsonValue<T>(T value)
{
    Console.WriteLine(JsonSerializer.Serialize(value, CreateJsonOptions(writeIndented: true)));
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

internal sealed record VpnCommandResult(bool Success, string Message, object? Data = null);

internal sealed record VpnSubscription
{
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = "ExApp VPN";
    public IReadOnlyList<VpnNode> Nodes { get; init; } = [];
}

internal sealed record VpnNode
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Location { get; init; }
    public JsonElement Outbound { get; init; }
}
