using System.Diagnostics;

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
    !File.Exists(Path.Combine(stagingPath, "ExApp.Desktop.exe")) ||
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

try
{
    await WaitForExitAsync(desktopPid, TimeSpan.FromSeconds(30));
    StopProcesses("ExApp.Agent");

    if (Directory.Exists(backupPath))
    {
        Directory.Delete(backupPath, recursive: true);
    }
    CopyDirectory(targetPath, backupPath, overwrite: true);
    ClearDirectory(targetPath);
    CopyDirectory(stagingPath, targetPath, overwrite: true);

    var desktopPath = Path.Combine(targetPath, "ExApp.Desktop.exe");
    if (!IsPortableExecutable(desktopPath))
    {
        throw new InvalidOperationException("Updated ExApp executable is invalid.");
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

    await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.Now:o} Update completed.{Environment.NewLine}");
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
        if (Directory.Exists(backupPath))
        {
            ClearDirectory(targetPath);
            CopyDirectory(backupPath, targetPath, overwrite: true);
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

static async Task WaitForExitAsync(int processId, TimeSpan timeout)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        using var cancellation = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cancellation.Token);
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

static void CopyDirectory(string source, string destination, bool overwrite)
{
    Directory.CreateDirectory(destination);
    foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    }

    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite);
    }
}

static void ClearDirectory(string path)
{
    Directory.CreateDirectory(path);
    foreach (var file in Directory.EnumerateFiles(path))
    {
        File.Delete(file);
    }

    foreach (var directory in Directory.EnumerateDirectories(path))
    {
        Directory.Delete(directory, recursive: true);
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

static Task AppendHistoryAsync(string path, string version, string status, string? message)
{
    var entry = System.Text.Json.JsonSerializer.Serialize(new
    {
        Timestamp = DateTimeOffset.Now,
        Component = "ExApp",
        Version = version,
        Status = status,
        Message = message
    });
    return File.AppendAllTextAsync(path, entry + Environment.NewLine);
}
