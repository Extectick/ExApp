using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

var arguments = ParseArguments(args);
var releaseVersion = arguments.GetValueOrDefault("version") ?? "unknown";
if (!arguments.TryGetValue("staging", out var stagingPath) ||
    !arguments.TryGetValue("target", out var targetPath) ||
    !arguments.TryGetValue("desktop-pid", out var desktopPidText) ||
    !int.TryParse(desktopPidText, out var desktopPid))
{
    return 2;
}

stagingPath = Path.GetFullPath(stagingPath);
targetPath = Path.GetFullPath(targetPath);
if (!Directory.Exists(stagingPath) ||
    Path.GetPathRoot(targetPath)?.Equals(targetPath, StringComparison.OrdinalIgnoreCase) == true ||
    stagingPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
{
    return 3;
}

var updateRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ExApp",
    "updates");
var backupPath = Path.Combine(updateRoot, "backup");
var logPath = Path.Combine(updateRoot, "updater.log");
var historyPath = Path.Combine(updateRoot, "history.jsonl");
Directory.CreateDirectory(updateRoot);

UpdatePlan? plan = null;

try
{
    var manifest = await LoadFileManifestAsync(stagingPath);
    var delta = await LoadDeltaManifestAsync(stagingPath);
    ValidateDeltaManifest(delta, manifest);
    await ValidateStagingAsync(stagingPath, manifest, delta);

    var stagedDesktopPath = Path.Combine(stagingPath, "ExApp.Desktop.exe");
    if (File.Exists(stagedDesktopPath) && !IsPortableExecutable(stagedDesktopPath))
    {
        throw new InvalidOperationException("Updated ExApp executable is invalid.");
    }

    await WaitForExitOrKillAsync(desktopPid, TimeSpan.FromSeconds(10));
    StopProcesses("ExApp.Agent");

    if (Directory.Exists(backupPath))
    {
        Directory.Delete(backupPath, recursive: true);
    }
    Directory.CreateDirectory(backupPath);

    plan = await BuildPlanAsync(targetPath, manifest, delta);
    await BackupPlanAsync(targetPath, backupPath, plan);
    await WritePlanAsync(backupPath, plan);
    await ApplyPlanAsync(stagingPath, targetPath, plan);
    CopyAppFileManifest(stagingPath, targetPath);
    DeleteEmptyDirectories(targetPath);

    var desktopPath = Path.Combine(targetPath, "ExApp.Desktop.exe");
    if (!IsPortableExecutable(desktopPath))
    {
        throw new InvalidOperationException("Updated ExApp executable is invalid after apply.");
    }

    var process = Process.Start(new ProcessStartInfo(desktopPath)
    {
        UseShellExecute = false,
        WorkingDirectory = targetPath
    }) ?? throw new InvalidOperationException("Updated ExApp process could not be started.");

    await Task.Delay(TimeSpan.FromSeconds(5));
    if (process.HasExited)
    {
        throw new InvalidOperationException($"Updated ExApp exited with code {process.ExitCode}.");
    }

    await File.AppendAllTextAsync(
        logPath,
        $"{DateTimeOffset.Now:o} Update completed. Copied {plan.Copy.Count}, deleted {plan.Delete.Count}, unchanged {plan.UnchangedCount}.{Environment.NewLine}");
    await AppendHistoryAsync(historyPath, releaseVersion, "installed", null);
    return 0;
}
catch (Exception exception)
{
    await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.Now:o} Update failed: {exception}{Environment.NewLine}");
    try
    {
        StopProcesses("ExApp.Desktop");
        StopProcesses("ExApp.Agent");
        plan ??= await TryReadPlanAsync(backupPath);
        if (plan is not null)
        {
            await RollbackPlanAsync(targetPath, backupPath, plan);
        }

        var desktopPath = Path.Combine(targetPath, "ExApp.Desktop.exe");
        if (File.Exists(desktopPath))
        {
            Process.Start(new ProcessStartInfo(desktopPath)
            {
                UseShellExecute = false,
                WorkingDirectory = targetPath
            });
        }
    }
    catch (Exception rollbackException)
    {
        await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.Now:o} Rollback failed: {rollbackException}{Environment.NewLine}");
    }

    await AppendHistoryAsync(historyPath, releaseVersion, "rolled-back", exception.Message);
    return 1;
}

static Dictionary<string, string> ParseArguments(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index + 1 < values.Length; index += 2)
    {
        result[values[index].TrimStart('-')] = values[index + 1];
    }

    return result;
}

static async Task<AppFileManifest> LoadFileManifestAsync(string root)
{
    var path = Path.Combine(root, "app-files.json");
    if (!File.Exists(path))
    {
        throw new FileNotFoundException("Application file manifest app-files.json is missing.", path);
    }

    await using var stream = File.OpenRead(path);
    var manifest = await JsonSerializer.DeserializeAsync<AppFileManifest>(
        stream,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (manifest is null || manifest.ManifestVersion != 1 || manifest.Files.Count == 0)
    {
        throw new InvalidOperationException("Application file manifest is invalid.");
    }

    foreach (var file in manifest.Files)
    {
        ValidateRelativePath(file.Path);
        if (file.Size < 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException($"Application file manifest entry '{file.Path}' is invalid.");
        }
    }

    return manifest;
}

static async Task<AppDeltaManifest?> LoadDeltaManifestAsync(string root)
{
    var path = Path.Combine(root, "app-delta.json");
    if (!File.Exists(path))
    {
        return null;
    }

    await using var stream = File.OpenRead(path);
    var manifest = await JsonSerializer.DeserializeAsync<AppDeltaManifest>(
        stream,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (manifest is null || manifest.ManifestVersion != 1)
    {
        throw new InvalidOperationException("Application delta manifest is invalid.");
    }

    foreach (var file in manifest.Files)
    {
        ValidateRelativePath(file.Path);
        if (file.Size < 0 || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException($"Application delta manifest entry '{file.Path}' is invalid.");
        }
    }

    foreach (var pathToDelete in manifest.Delete)
    {
        ValidateRelativePath(pathToDelete);
    }

    return manifest;
}

static void ValidateDeltaManifest(AppDeltaManifest? delta, AppFileManifest manifest)
{
    if (delta is null)
    {
        return;
    }

    if (!delta.Version.Equals(manifest.Version, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Application delta manifest target version does not match app-files.json.");
    }

    var desired = manifest.Files.ToDictionary(
        static file => NormalizeRelativePath(file.Path),
        static file => file,
        StringComparer.OrdinalIgnoreCase);
    foreach (var file in delta.Files)
    {
        var relativePath = NormalizeRelativePath(file.Path);
        if (!desired.TryGetValue(relativePath, out var expected) ||
            !expected.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) ||
            expected.Size != file.Size)
        {
            throw new InvalidOperationException($"Application delta file '{file.Path}' is not part of the target manifest.");
        }
    }

    foreach (var pathToDelete in delta.Delete.Select(NormalizeRelativePath))
    {
        if (desired.ContainsKey(pathToDelete))
        {
            throw new InvalidOperationException($"Application delta delete path '{pathToDelete}' is still part of the target manifest.");
        }
    }
}

static async Task ValidateStagingAsync(string stagingRoot, AppFileManifest manifest, AppDeltaManifest? delta)
{
    var payloadFiles = delta?.Files ?? manifest.Files;
    foreach (var file in payloadFiles)
    {
        var path = GetSafePath(stagingRoot, file.Path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Staged application file '{file.Path}' is missing.", path);
        }

        var info = new FileInfo(path);
        if (info.Length != file.Size)
        {
            throw new InvalidOperationException($"Staged application file '{file.Path}' has an invalid size.");
        }

        var hash = await ComputeSha256Async(path);
        if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Staged application file '{file.Path}' has an invalid SHA-256.");
        }
    }
}

static async Task<UpdatePlan> BuildPlanAsync(string targetRoot, AppFileManifest manifest, AppDeltaManifest? delta)
{
    var desired = manifest.Files.ToDictionary(
        static file => NormalizeRelativePath(file.Path),
        static file => file,
        StringComparer.OrdinalIgnoreCase);
    var copy = new List<CopyOperation>();
    var unchanged = 0;
    var copyCandidates = delta?.Files ?? manifest.Files;

    foreach (var file in copyCandidates)
    {
        var relativePath = NormalizeRelativePath(file.Path);
        var targetPath = GetSafePath(targetRoot, relativePath);
        if (File.Exists(targetPath) &&
            new FileInfo(targetPath).Length == file.Size &&
            (await ComputeSha256Async(targetPath)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            unchanged++;
            continue;
        }

        copy.Add(new CopyOperation(relativePath, File.Exists(targetPath)));
    }

    var delete = new List<string>();
    if (delta is not null)
    {
        var copyPaths = new HashSet<string>(
            copyCandidates.Select(static file => NormalizeRelativePath(file.Path)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var relativePath = NormalizeRelativePath(file.Path);
            if (copyPaths.Contains(relativePath))
            {
                continue;
            }

            var targetPath = GetSafePath(targetRoot, relativePath);
            if (!File.Exists(targetPath) ||
                new FileInfo(targetPath).Length != file.Size ||
                !(await ComputeSha256Async(targetPath)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Delta update cannot be applied because installed file '{relativePath}' does not match the target manifest.");
            }
        }

        delete.AddRange(delta.Delete.Select(NormalizeRelativePath));
        unchanged = Math.Max(0, manifest.Files.Count - copy.Count);
    }
    else if (Directory.Exists(targetRoot))
    {
        foreach (var file in Directory.EnumerateFiles(targetRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(targetRoot, file));
            if (relativePath.Equals("app-files.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!desired.ContainsKey(relativePath))
            {
                delete.Add(relativePath);
            }
        }
    }

    return new UpdatePlan(copy, delete, unchanged);
}

static async Task BackupPlanAsync(string targetRoot, string backupRoot, UpdatePlan plan)
{
    var filesRoot = Path.Combine(backupRoot, "files");
    Directory.CreateDirectory(filesRoot);
    var currentManifestPath = Path.Combine(targetRoot, "app-files.json");
    if (File.Exists(currentManifestPath))
    {
        File.Copy(currentManifestPath, Path.Combine(backupRoot, "app-files.json"), overwrite: true);
    }

    foreach (var relativePath in plan.Copy.Where(static operation => operation.HadExistingFile).Select(static operation => operation.Path).Concat(plan.Delete))
    {
        var source = GetSafePath(targetRoot, relativePath);
        if (!File.Exists(source))
        {
            continue;
        }

        var destination = GetSafePath(filesRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output);
    }
}

static async Task ApplyPlanAsync(string stagingRoot, string targetRoot, UpdatePlan plan)
{
    foreach (var operation in plan.Copy)
    {
        var source = GetSafePath(stagingRoot, operation.Path);
        var destination = GetSafePath(targetRoot, operation.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    foreach (var relativePath in plan.Delete)
    {
        var path = GetSafePath(targetRoot, relativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    await Task.CompletedTask;
}

static async Task RollbackPlanAsync(string targetRoot, string backupRoot, UpdatePlan plan)
{
    var filesRoot = Path.Combine(backupRoot, "files");
    foreach (var operation in plan.Copy.Where(static operation => !operation.HadExistingFile))
    {
        var target = GetSafePath(targetRoot, operation.Path);
        if (File.Exists(target))
        {
            File.Delete(target);
        }
    }

    foreach (var relativePath in plan.Copy.Where(static operation => operation.HadExistingFile).Select(static operation => operation.Path).Concat(plan.Delete))
    {
        var backup = GetSafePath(filesRoot, relativePath);
        if (!File.Exists(backup))
        {
            continue;
        }

        var target = GetSafePath(targetRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var input = File.Open(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = File.Create(target);
        await input.CopyToAsync(output);
    }

    var manifestBackup = Path.Combine(backupRoot, "app-files.json");
    var targetManifest = Path.Combine(targetRoot, "app-files.json");
    if (File.Exists(manifestBackup))
    {
        File.Copy(manifestBackup, targetManifest, overwrite: true);
    }
    else if (File.Exists(targetManifest))
    {
        File.Delete(targetManifest);
    }

    DeleteEmptyDirectories(targetRoot);
}

static void CopyAppFileManifest(string stagingRoot, string targetRoot)
{
    File.Copy(
        Path.Combine(stagingRoot, "app-files.json"),
        Path.Combine(targetRoot, "app-files.json"),
        overwrite: true);
}

static Task WritePlanAsync(string backupRoot, UpdatePlan plan)
{
    var path = Path.Combine(backupRoot, "update-plan.json");
    var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    });
    return File.WriteAllTextAsync(path, json);
}

static async Task<UpdatePlan?> TryReadPlanAsync(string backupRoot)
{
    var path = Path.Combine(backupRoot, "update-plan.json");
    if (!File.Exists(path))
    {
        return null;
    }

    await using var stream = File.OpenRead(path);
    return await JsonSerializer.DeserializeAsync<UpdatePlan>(
        stream,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

static async Task WaitForExitOrKillAsync(int processId, TimeSpan timeout)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
    catch (ArgumentException)
    {
    }
}

static void StopProcesses(string name)
{
    foreach (var process in Process.GetProcessesByName(name))
    {
        using (process)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10000);
        }
    }
}

static bool IsPortableExecutable(string path)
{
    if (!File.Exists(path))
    {
        return false;
    }

    using var stream = File.OpenRead(path);
    return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
}

static async Task<string> ComputeSha256Async(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
}

static string GetSafePath(string root, string relativePath)
{
    ValidateRelativePath(relativePath);
    var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Path '{relativePath}' escapes the application directory.");
    }

    return fullPath;
}

static void ValidateRelativePath(string relativePath)
{
    if (string.IsNullOrWhiteSpace(relativePath) ||
        Path.IsPathRooted(relativePath) ||
        relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(static segment => segment == ".."))
    {
        throw new InvalidOperationException($"Path '{relativePath}' is not a safe relative path.");
    }
}

static string NormalizeRelativePath(string relativePath) =>
    relativePath.Replace('\\', '/');

static void DeleteEmptyDirectories(string root)
{
    if (!Directory.Exists(root))
    {
        return;
    }

    foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                 .OrderByDescending(static directory => directory.Length))
    {
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }
}

static Task AppendHistoryAsync(string path, string version, string status, string? message)
{
    var entry = JsonSerializer.Serialize(new
    {
        Timestamp = DateTimeOffset.Now,
        Component = "ExApp",
        Version = version,
        Status = status,
        Message = message
    });
    return File.AppendAllTextAsync(path, entry + Environment.NewLine);
}

internal sealed record AppFileManifest(
    int ManifestVersion,
    string Version,
    DateTimeOffset GeneratedAt,
    List<AppFileEntry> Files);

internal sealed record AppFileEntry(
    string Path,
    string Sha256,
    long Size);

internal sealed record AppDeltaManifest(
    int ManifestVersion,
    string BaseVersion,
    string Version,
    DateTimeOffset GeneratedAt,
    List<AppFileEntry> Files,
    List<string> Delete);

internal sealed record UpdatePlan(
    List<CopyOperation> Copy,
    List<string> Delete,
    int UnchangedCount);

internal sealed record CopyOperation(
    string Path,
    bool HadExistingFile);
