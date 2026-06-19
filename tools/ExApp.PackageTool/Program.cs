using System.Text.Json;
using ExApp.Packaging;

var command = args.FirstOrDefault()?.ToLowerInvariant();
var root = GetOption(args, "--root")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExApp");
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true
};
var manager = new PackageManager(new PackageManagerOptions
{
    RootDirectory = root,
    AppVersion = "0.1.0",
    AgentVersion = "0.1.0",
    Architecture = GetOption(args, "--architecture") ?? "x64"
});

try
{
    return command switch
    {
        "install" => await InstallAsync(),
        "update" => await UpdateAsync(),
        "list" => ListInstalled(),
        "state" => ShowState(),
        "rollback" => await RollbackAsync(),
        "uninstall" => await UninstallAsync(),
        _ => ShowUsage()
    };
}
catch (PackageException exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        error = exception.Code,
        message = exception.Message
    }, jsonOptions));
    return 2;
}

async Task<int> InstallAsync()
{
    var packagePath = GetPositionalArgument(args, 1)
        ?? throw new PackageException("command.packageRequired", "Package path is required.");
    var result = await manager.InstallAsync(packagePath, GetOption(args, "--sha256"));
    WriteJson(new
    {
        result.Manifest.Id,
        result.Manifest.Version,
        result.InstallDirectory,
        result.AlreadyInstalled,
        result.State.PreviousVersion
    });
    return 0;
}

async Task<int> UpdateAsync()
{
    var packagePath = GetPositionalArgument(args, 1)
        ?? throw new PackageException("command.packageRequired", "Package path is required.");
    var result = args.Contains("--delta", StringComparer.OrdinalIgnoreCase)
        ? await manager.InstallDeltaAsync(packagePath, GetOption(args, "--sha256"))
        : await manager.InstallAsync(packagePath, GetOption(args, "--sha256"));
    WriteJson(new
    {
        result.Manifest.Id,
        result.Manifest.Version,
        result.InstallDirectory,
        result.AlreadyInstalled,
        result.AppliedDelta,
        result.CopiedFiles,
        result.LinkedFiles,
        result.DeletedFiles,
        result.State.PreviousVersion
    });
    return 0;
}

int ListInstalled()
{
    WriteJson(manager.GetInstalledServices());
    return 0;
}

int ShowState()
{
    var serviceId = GetPositionalArgument(args, 1)
        ?? throw new PackageException("command.serviceRequired", "Service id is required.");
    WriteJson(manager.GetState(serviceId));
    return 0;
}

async Task<int> RollbackAsync()
{
    var serviceId = GetPositionalArgument(args, 1)
        ?? throw new PackageException("command.serviceRequired", "Service id is required.");
    WriteJson(await manager.RollbackAsync(serviceId));
    return 0;
}

async Task<int> UninstallAsync()
{
    var serviceId = GetPositionalArgument(args, 1)
        ?? throw new PackageException("command.serviceRequired", "Service id is required.");
    await manager.UninstallAsync(serviceId, args.Contains("--delete-data", StringComparer.OrdinalIgnoreCase));
    WriteJson(new { serviceId, uninstalled = true });
    return 0;
}

static int ShowUsage()
{
    Console.WriteLine(
        """
        ExApp Package Tool
          install <package.svcpkg> [--sha256 <hash>] [--root <path>]
          update <package.svcpkg|package.svcdelta> [--delta] [--sha256 <hash>] [--root <path>]
          list [--root <path>]
          state <service-id> [--root <path>]
          rollback <service-id> [--root <path>]
          uninstall <service-id> [--delete-data] [--root <path>]
        """);
    return 1;
}

static string? GetOption(string[] arguments, string name)
{
    var index = Array.FindIndex(arguments, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static string? GetPositionalArgument(string[] arguments, int position)
{
    var positional = new List<string>();
    for (var index = 0; index < arguments.Length; index++)
    {
        if (arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            if (!IsFlagOption(arguments[index]))
            {
                index++;
            }

            continue;
        }

        positional.Add(arguments[index]);
    }

    return position < positional.Count ? positional[position] : null;
}

static bool IsFlagOption(string argument) =>
    argument.Equals("--delete-data", StringComparison.OrdinalIgnoreCase) ||
    argument.Equals("--delta", StringComparison.OrdinalIgnoreCase);

void WriteJson<T>(T value) =>
    Console.WriteLine(JsonSerializer.Serialize(value, jsonOptions));
