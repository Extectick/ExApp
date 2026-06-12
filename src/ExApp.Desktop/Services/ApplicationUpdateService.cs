using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using ExApp.Core.Updates;

namespace ExApp.Desktop.Services;

internal sealed class ApplicationUpdateService
{
    private readonly AppUpdateClient _client = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public static ApplicationUpdateService Current { get; } = new();

    public AppUpdateCheckResult? LastCheck { get; private set; }
    public string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";

    public async Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            LastCheck = await _client.CheckAsync(CurrentVersion, AppSettings.UpdateChannel, cancellationToken);
            await UpdateHistoryStore.AddAsync(new UpdateHistoryEntry(
                DateTimeOffset.Now,
                "ExApp",
                LastCheck.Release?.Version ?? CurrentVersion,
                LastCheck.IsUpdateAvailable ? "available" : "current",
                null));
            return LastCheck;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task PrepareAndLaunchAsync(
        AppReleaseManifest release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var updateRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExApp",
                "updates",
                release.Version);
            Directory.CreateDirectory(updateRoot);
            var archivePath = await _client.DownloadAsync(release, updateRoot, progress, cancellationToken);
            await UpdateHistoryStore.AddAsync(new UpdateHistoryEntry(
                DateTimeOffset.Now,
                "ExApp",
                release.Version,
                "installing",
                null));
            var stagingPath = Path.Combine(updateRoot, "staging");
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
            ZipFile.ExtractToDirectory(archivePath, stagingPath);

            var updaterSource = FindUpdaterExecutable()
                ?? throw new FileNotFoundException("ExApp.Updater.exe was not found.");
            var runnerPath = Path.Combine(updateRoot, "runner");
            Directory.CreateDirectory(runnerPath);
            foreach (var file in Directory.EnumerateFiles(
                         Path.GetDirectoryName(updaterSource)!,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                File.Copy(file, Path.Combine(runnerPath, Path.GetFileName(file)), overwrite: true);
            }

            var updaterPath = Path.Combine(runnerPath, "ExApp.Updater.exe");
            _ = Process.Start(new ProcessStartInfo(updaterPath)
            {
                UseShellExecute = true,
                WorkingDirectory = runnerPath,
                ArgumentList =
                {
                    "--staging", stagingPath,
                    "--target", AppContext.BaseDirectory,
                    "--desktop-pid", Environment.ProcessId.ToString(),
                    "--version", release.Version
                }
            }) ?? throw new InvalidOperationException("ExApp Updater could not be started.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private static string? FindUpdaterExecutable()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ExApp.Updater.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "src",
                "ExApp.Updater",
                "bin",
                "Debug",
                "net8.0",
                "win-x64",
                "ExApp.Updater.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }
}
