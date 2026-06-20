using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

var arguments = ParseArguments(args);
var releaseVersion = arguments.GetValueOrDefault("version") ?? "unknown";
var recoveryMode = arguments.ContainsKey("recover");
var noRestart = arguments.TryGetValue("no-restart", out var noRestartText) &&
                bool.TryParse(noRestartText, out var noRestartValue) &&
                noRestartValue;
if (!arguments.TryGetValue("target", out var targetPath) ||
    !arguments.TryGetValue("desktop-pid", out var desktopPidText) ||
    !int.TryParse(desktopPidText, out var desktopPid))
{
    return 2;
}

targetPath = Path.GetFullPath(targetPath);
if (Path.GetPathRoot(targetPath)?.Equals(targetPath, StringComparison.OrdinalIgnoreCase) == true)
{
    return 3;
}

string? stagingPath = null;
string? fallbackStagingPath = null;
if (!recoveryMode)
{
    if (!arguments.TryGetValue("staging", out stagingPath))
    {
        return 2;
    }

    stagingPath = Path.GetFullPath(stagingPath);
    if (!Directory.Exists(stagingPath) ||
        stagingPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
    {
        return 3;
    }

    if (arguments.TryGetValue("fallback-staging", out var fallbackStagingArgument) &&
        !string.IsNullOrWhiteSpace(fallbackStagingArgument))
    {
        fallbackStagingPath = Path.GetFullPath(fallbackStagingArgument);
        if (!Directory.Exists(fallbackStagingPath) ||
            fallbackStagingPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase) ||
            fallbackStagingPath.Equals(stagingPath, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }
    }
}

var updateRoot = arguments.TryGetValue("update-root", out var updateRootArgument)
    ? Path.GetFullPath(updateRootArgument)
    : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExApp",
        "updates");
var backupPath = Path.Combine(updateRoot, "backup");
var logPath = Path.Combine(updateRoot, "updater.log");
var historyPath = Path.Combine(updateRoot, "history.jsonl");
var statePath = Path.Combine(updateRoot, "update-state.json");
Directory.CreateDirectory(updateRoot);

UpdatePlan? plan = null;

try
{
    if (recoveryMode)
    {
        await WaitForExitOrKillAsync(desktopPid, TimeSpan.FromSeconds(10));
        StopProcesses("ExApp.Agent");
        plan = await TryReadPlanAsync(backupPath)
            ?? throw new InvalidOperationException("Interrupted update backup plan is missing.");
        await RollbackPlanAsync(targetPath, backupPath, plan);
        await AppendHistoryAsync(historyPath, releaseVersion, "recovered", "Interrupted update was rolled back on next launch.");
        await WriteStateAsync(statePath, new UpdateState(releaseVersion, targetPath, "recovered", DateTimeOffset.UtcNow, null));
        TryDeleteDirectory(backupPath);
        if (!noRestart)
        {
            StartDesktop(targetPath);
        }

        return 0;
    }

    if (stagingPath is null)
    {
        return 2;
    }

    var manifest = await LoadFileManifestAsync(stagingPath);
    var delta = await LoadDeltaManifestAsync(stagingPath);
    ValidateDeltaManifest(delta, manifest);
    await ValidateStagingAsync(stagingPath, manifest, delta);

    var stagedDesktopPath = Path.Combine(stagingPath, "ExApp.Desktop.exe");
    if (File.Exists(stagedDesktopPath) && !IsPortableExecutable(stagedDesktopPath))
    {
        throw new InvalidOperationException("Updated ExApp executable is invalid.");
    }

    try
    {
        plan = await BuildPlanAsync(targetPath, manifest, delta);
    }
    catch (Exception exception) when (delta is not null && IsDeltaFallbackCandidate(exception))
    {
        fallbackStagingPath ??= await TryPrepareFallbackStagingAsync(
            arguments,
            updateRoot,
            releaseVersion,
            logPath);
        if (fallbackStagingPath is null)
        {
            throw;
        }

        await File.AppendAllTextAsync(
            logPath,
            $"{DateTimeOffset.Now:o} Delta update cannot be applied, falling back to full package: {exception.Message}{Environment.NewLine}");
        stagingPath = fallbackStagingPath;
        manifest = await LoadFileManifestAsync(stagingPath);
        delta = await LoadDeltaManifestAsync(stagingPath);
        if (delta is not null)
        {
            throw new InvalidOperationException("Application fallback staging must contain a full package, but app-delta.json was found.");
        }

        ValidateDeltaManifest(delta, manifest);
        await ValidateStagingAsync(stagingPath, manifest, delta);
        stagedDesktopPath = Path.Combine(stagingPath, "ExApp.Desktop.exe");
        if (!File.Exists(stagedDesktopPath) || !IsPortableExecutable(stagedDesktopPath))
        {
            throw new InvalidOperationException("Fallback ExApp executable is invalid.");
        }

        plan = await BuildPlanAsync(targetPath, manifest, delta);
    }

    await WaitForExitOrKillAsync(desktopPid, TimeSpan.FromSeconds(10));
    StopProcesses("ExApp.Agent");

    if (Directory.Exists(backupPath))
    {
        Directory.Delete(backupPath, recursive: true);
    }
    Directory.CreateDirectory(backupPath);

    await BackupPlanAsync(targetPath, backupPath, plan);
    await WritePlanAsync(backupPath, plan);
    await WriteStateAsync(statePath, new UpdateState(releaseVersion, targetPath, "applying", DateTimeOffset.UtcNow, null));
    await ApplyPlanAsync(stagingPath, targetPath, plan);
    CopyAppFileManifest(stagingPath, targetPath);
    DeleteEmptyDirectories(targetPath);

    var desktopPath = Path.Combine(targetPath, "ExApp.Desktop.exe");
    if (!IsPortableExecutable(desktopPath))
    {
        throw new InvalidOperationException("Updated ExApp executable is invalid after apply.");
    }

    if (!noRestart)
    {
        var process = StartDesktop(targetPath);

        await Task.Delay(TimeSpan.FromSeconds(5));
        if (process.HasExited)
        {
            throw new InvalidOperationException($"Updated ExApp exited with code {process.ExitCode}.");
        }
    }

    await File.AppendAllTextAsync(
        logPath,
        $"{DateTimeOffset.Now:o} Update completed. Copied {plan.Copy.Count}, deleted {plan.Delete.Count}, unchanged {plan.UnchangedCount}.{Environment.NewLine}");
    await AppendHistoryAsync(historyPath, releaseVersion, "installed", null);
    await WriteStateAsync(statePath, new UpdateState(releaseVersion, targetPath, "installed", DateTimeOffset.UtcNow, null));
    TryDeleteDirectory(backupPath);
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

        StartDesktopIfExists(targetPath);
    }
    catch (Exception rollbackException)
    {
        await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.Now:o} Rollback failed: {rollbackException}{Environment.NewLine}");
    }

    await AppendHistoryAsync(historyPath, releaseVersion, "rolled-back", exception.Message);
    await WriteStateAsync(statePath, new UpdateState(releaseVersion, targetPath, "rolled-back", DateTimeOffset.UtcNow, exception.Message));
    TryDeleteDirectory(backupPath);
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

    foreach (var patch in delta.Patches ?? [])
    {
        var relativePath = NormalizeRelativePath(patch.Path);
        ValidateRelativePath(patch.DataPath);
        if (!desired.TryGetValue(relativePath, out var expected) ||
            !expected.Sha256.Equals(patch.TargetSha256, StringComparison.OrdinalIgnoreCase) ||
            expected.Size != patch.TargetSize)
        {
            throw new InvalidOperationException($"Application delta patch '{patch.Path}' is not part of the target manifest.");
        }

        if (patch.BlockSize <= 0 ||
            patch.BaseSize < 0 ||
            patch.BaseSha256.Length != 64 ||
            !patch.BaseSha256.All(Uri.IsHexDigit) ||
            patch.TargetSize < 0 ||
            patch.TargetSha256.Length != 64 ||
            !patch.TargetSha256.All(Uri.IsHexDigit) ||
            patch.Operations.Count == 0)
        {
            throw new InvalidOperationException($"Application delta patch '{patch.Path}' is invalid.");
        }

        foreach (var operation in patch.Operations)
        {
            if (operation.Length <= 0 ||
                operation.Offset < 0 ||
                operation.DataOffset < 0 ||
                operation.Type is not ("copy" or "data"))
            {
                throw new InvalidOperationException($"Application delta patch operation for '{patch.Path}' is invalid.");
            }

            if (operation.Type.Equals("copy", StringComparison.OrdinalIgnoreCase) &&
                !IsRangeWithin(operation.Offset, operation.Length, patch.BaseSize))
            {
                throw new InvalidOperationException($"Application delta patch copy operation for '{patch.Path}' exceeds the base file.");
            }
        }

        var outputLength = patch.Operations.Sum(static operation => (long)operation.Length);
        if (outputLength != patch.TargetSize)
        {
            throw new InvalidOperationException($"Application delta patch '{patch.Path}' does not produce the target file size.");
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

    foreach (var patch in delta?.Patches ?? [])
    {
        var dataPath = GetSafePath(stagingRoot, patch.DataPath);
        if (!File.Exists(dataPath))
        {
            throw new FileNotFoundException($"Staged application patch data '{patch.DataPath}' is missing.", dataPath);
        }

        var dataSize = new FileInfo(dataPath).Length;
        foreach (var operation in patch.Operations.Where(static operation => operation.Type.Equals("data", StringComparison.OrdinalIgnoreCase)))
        {
            if (!IsRangeWithin(operation.DataOffset, operation.Length, dataSize))
            {
                throw new InvalidOperationException($"Application delta patch data operation for '{patch.Path}' exceeds '{patch.DataPath}'.");
            }
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
    var patch = new List<PatchOperation>();
    var unchanged = 0;
    var copyCandidates = delta?.Files ?? manifest.Files;
    var patchCandidates = delta?.Patches ?? [];

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

    foreach (var patchEntry in patchCandidates)
    {
        var relativePath = NormalizeRelativePath(patchEntry.Path);
        var targetPath = GetSafePath(targetRoot, relativePath);
        if (File.Exists(targetPath) &&
            new FileInfo(targetPath).Length == patchEntry.TargetSize &&
            (await ComputeSha256Async(targetPath)).Equals(patchEntry.TargetSha256, StringComparison.OrdinalIgnoreCase))
        {
            unchanged++;
            continue;
        }

        if (!File.Exists(targetPath) ||
            new FileInfo(targetPath).Length != patchEntry.BaseSize ||
            !(await ComputeSha256Async(targetPath)).Equals(patchEntry.BaseSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Delta patch cannot be applied because installed file '{relativePath}' does not match the patch base.");
        }

        patch.Add(new PatchOperation(relativePath, patchEntry.DataPath, File.Exists(targetPath), patchEntry));
    }

    var delete = new List<string>();
    if (delta is not null)
    {
        var payloadPaths = new HashSet<string>(
            copyCandidates.Select(static file => NormalizeRelativePath(file.Path))
                .Concat(patchCandidates.Select(static item => NormalizeRelativePath(item.Path))),
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var relativePath = NormalizeRelativePath(file.Path);
            if (payloadPaths.Contains(relativePath))
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
        unchanged = Math.Max(0, manifest.Files.Count - copy.Count - patch.Count);
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

    return new UpdatePlan(copy, patch, delete, unchanged);
}

static bool IsDeltaFallbackCandidate(Exception exception) =>
    exception is InvalidOperationException or FileNotFoundException or IOException;

static async Task<string?> TryPrepareFallbackStagingAsync(
    Dictionary<string, string> arguments,
    string updateRoot,
    string releaseVersion,
    string logPath)
{
    if (!arguments.TryGetValue("fallback-package-url", out var fallbackPackageUrl) ||
        string.IsNullOrWhiteSpace(fallbackPackageUrl) ||
        !arguments.TryGetValue("fallback-package-sha256", out var fallbackPackageSha256) ||
        string.IsNullOrWhiteSpace(fallbackPackageSha256) ||
        !arguments.TryGetValue("fallback-package-size", out var fallbackPackageSizeText) ||
        !long.TryParse(fallbackPackageSizeText, out var fallbackPackageSize) ||
        fallbackPackageSize <= 0)
    {
        return null;
    }

    if (fallbackPackageSha256.Length != 64 || !fallbackPackageSha256.All(Uri.IsHexDigit))
    {
        return null;
    }

    var fallbackRoot = Path.Combine(updateRoot, "fallback");
    Directory.CreateDirectory(fallbackRoot);
    var packagePath = Path.Combine(
        fallbackRoot,
        GetPackageFileName(fallbackPackageUrl, releaseVersion));
    var fallbackStagingPath = Path.Combine(updateRoot, "fallback-staging");

    await File.AppendAllTextAsync(
        logPath,
        $"{DateTimeOffset.Now:o} Preparing lazy full fallback package.{Environment.NewLine}");
    await DownloadOrCopyPackageAsync(
        fallbackPackageUrl,
        packagePath,
        fallbackPackageSha256,
        fallbackPackageSize);

    if (Directory.Exists(fallbackStagingPath))
    {
        Directory.Delete(fallbackStagingPath, recursive: true);
    }

    ZipFile.ExtractToDirectory(packagePath, fallbackStagingPath);
    return fallbackStagingPath;
}

static async Task DownloadOrCopyPackageAsync(
    string source,
    string targetPath,
    string expectedSha256,
    long expectedSize)
{
    if (File.Exists(targetPath) &&
        new FileInfo(targetPath).Length == expectedSize &&
        (await ComputeSha256Async(targetPath)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var temporaryPath = $"{targetPath}.download";
    if (IsHttp(source))
    {
        if (File.Exists(temporaryPath) &&
            new FileInfo(temporaryPath).Length == expectedSize &&
            (await ComputeSha256Async(temporaryPath)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(temporaryPath, targetPath, overwrite: true);
            return;
        }

        await DownloadHttpWithResumeAsync(source, temporaryPath, expectedSize);
    }
    else
    {
        File.Copy(Path.GetFullPath(source), temporaryPath, overwrite: true);
    }

    var actualSize = new FileInfo(temporaryPath).Length;
    if (actualSize != expectedSize)
    {
        File.Delete(temporaryPath);
        throw new InvalidOperationException("Fallback application package has an invalid size.");
    }

    var actualHash = await ComputeSha256Async(temporaryPath);
    if (!actualHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
    {
        File.Delete(temporaryPath);
        throw new InvalidOperationException("Fallback application package SHA-256 does not match the release manifest.");
    }

    File.Move(temporaryPath, targetPath, overwrite: true);
}

static async Task DownloadHttpWithResumeAsync(
    string url,
    string temporaryPath,
    long expectedSize)
{
    const int maxAttempts = 4;
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            await DownloadHttpAttemptAsync(url, temporaryPath, expectedSize);
            return;
        }
        catch (Exception exception) when (
            attempt < maxAttempts &&
            exception is HttpRequestException or IOException or TaskCanceledException)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt * attempt));
        }
    }

    await DownloadHttpAttemptAsync(url, temporaryPath, expectedSize);
}

static async Task DownloadHttpAttemptAsync(
    string url,
    string temporaryPath,
    long expectedSize)
{
    var existingLength = File.Exists(temporaryPath) ? new FileInfo(temporaryPath).Length : 0;
    if (expectedSize > 0 && existingLength > expectedSize)
    {
        File.Delete(temporaryPath);
        existingLength = 0;
    }

    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ExApp.Updater/0.1");
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    if (existingLength > 0)
    {
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);
    }

    using var response = await client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead);
    if (existingLength > 0 && response.StatusCode == System.Net.HttpStatusCode.OK)
    {
        File.Delete(temporaryPath);
        existingLength = 0;
    }
    else if (existingLength > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
    {
        response.EnsureSuccessStatusCode();
        throw new IOException("The fallback package server did not accept a ranged download request.");
    }

    response.EnsureSuccessStatusCode();
    await using var remote = await response.Content.ReadAsStreamAsync();
    await using var local = new FileStream(
        temporaryPath,
        existingLength > 0 ? FileMode.Append : FileMode.Create,
        FileAccess.Write,
        FileShare.None,
        81920,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    await remote.CopyToAsync(local);
    await local.FlushAsync();
}

static string GetPackageFileName(string source, string version)
{
    if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
    {
        return Path.GetFileName(uri.AbsolutePath);
    }

    var fileName = Path.GetFileName(source);
    return string.IsNullOrWhiteSpace(fileName)
        ? $"exapp-{version}-win-x64.zip"
        : fileName;
}

static bool IsHttp(string value) =>
    Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

static async Task BackupPlanAsync(string targetRoot, string backupRoot, UpdatePlan plan)
{
    var filesRoot = Path.Combine(backupRoot, "files");
    Directory.CreateDirectory(filesRoot);
    var currentManifestPath = Path.Combine(targetRoot, "app-files.json");
    if (File.Exists(currentManifestPath))
    {
        File.Copy(currentManifestPath, Path.Combine(backupRoot, "app-files.json"), overwrite: true);
    }

    foreach (var relativePath in plan.Copy.Where(static operation => operation.HadExistingFile).Select(static operation => operation.Path)
                 .Concat(plan.Patch.Where(static operation => operation.HadExistingFile).Select(static operation => operation.Path))
                 .Concat(plan.Delete))
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

    foreach (var operation in plan.Patch)
    {
        var destination = GetSafePath(targetRoot, operation.Path);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            await ApplyPatchAsync(targetRoot, stagingRoot, operation.Patch, temporary);
            var hash = await ComputeSha256Async(temporary);
            if (!hash.Equals(operation.Patch.TargetSha256, StringComparison.OrdinalIgnoreCase) ||
                new FileInfo(temporary).Length != operation.Patch.TargetSize)
            {
                throw new InvalidOperationException($"Patched application file '{operation.Path}' failed validation.");
            }

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
    foreach (var operation in plan.Copy.Where(static operation => !operation.HadExistingFile)
                 .Concat(plan.Patch.Where(static operation => !operation.HadExistingFile).Select(static operation => new CopyOperation(operation.Path, operation.HadExistingFile))))
    {
        var target = GetSafePath(targetRoot, operation.Path);
        if (File.Exists(target))
        {
            File.Delete(target);
        }
    }

    foreach (var relativePath in plan.Copy.Where(static operation => operation.HadExistingFile).Select(static operation => operation.Path)
                 .Concat(plan.Patch.Where(static operation => operation.HadExistingFile).Select(static operation => operation.Path))
                 .Concat(plan.Delete))
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

static Process StartDesktop(string targetRoot)
{
    var desktopPath = Path.Combine(targetRoot, "ExApp.Desktop.exe");
    return Process.Start(new ProcessStartInfo(desktopPath)
    {
        UseShellExecute = false,
        WorkingDirectory = targetRoot
    }) ?? throw new InvalidOperationException("Updated ExApp process could not be started.");
}

static void StartDesktopIfExists(string targetRoot)
{
    var desktopPath = Path.Combine(targetRoot, "ExApp.Desktop.exe");
    if (!File.Exists(desktopPath))
    {
        return;
    }

    Process.Start(new ProcessStartInfo(desktopPath)
    {
        UseShellExecute = false,
        WorkingDirectory = targetRoot
    });
}

static async Task ApplyPatchAsync(
    string targetRoot,
    string stagingRoot,
    AppFilePatchEntry patch,
    string destination)
{
    var basePath = GetSafePath(targetRoot, patch.Path);
    var dataPath = GetSafePath(stagingRoot, patch.DataPath);
    if (!File.Exists(basePath))
    {
        throw new FileNotFoundException($"Base application file '{patch.Path}' is missing.", basePath);
    }

    if (new FileInfo(basePath).Length != patch.BaseSize)
    {
        throw new InvalidOperationException($"Base application file '{patch.Path}' has an invalid size.");
    }

    if (!(await ComputeSha256Async(basePath)).Equals(patch.BaseSha256, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Base application file '{patch.Path}' has an invalid SHA-256.");
    }

    if (!File.Exists(dataPath))
    {
        throw new FileNotFoundException($"Application patch data '{patch.DataPath}' is missing.", dataPath);
    }

    await using var baseStream = File.Open(basePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    await using var dataStream = File.Open(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    await using var output = File.Create(destination);
    var buffer = new byte[81920];
    foreach (var operation in patch.Operations)
    {
        Stream source;
        long offset;
        if (operation.Type.Equals("copy", StringComparison.OrdinalIgnoreCase))
        {
            source = baseStream;
            offset = operation.Offset;
        }
        else if (operation.Type.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            source = dataStream;
            offset = operation.DataOffset;
        }
        else
        {
            throw new InvalidOperationException($"Unsupported patch operation '{operation.Type}'.");
        }

        source.Seek(offset, SeekOrigin.Begin);
        if (!IsRangeWithin(offset, operation.Length, source.Length))
        {
            throw new EndOfStreamException($"Patch operation for '{patch.Path}' exceeded source length.");
        }

        var remaining = operation.Length;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)));
            if (read == 0)
            {
                throw new EndOfStreamException($"Patch operation for '{patch.Path}' exceeded source length.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }
}

static bool IsRangeWithin(long offset, int length, long sourceSize) =>
    offset >= 0 &&
    length > 0 &&
    sourceSize >= 0 &&
    offset <= sourceSize - length;

static Task WritePlanAsync(string backupRoot, UpdatePlan plan)
{
    var path = Path.Combine(backupRoot, "update-plan.json");
    var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
        });
    return File.WriteAllTextAsync(path, json);
}

static Task WriteStateAsync(string path, UpdateState state)
{
    var json = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)
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

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        // Stale backup cleanup must not turn a completed update into a failed update.
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
    List<AppFilePatchEntry>? Patches,
    List<string> Delete);

internal sealed record AppFilePatchEntry(
    string Path,
    int BlockSize,
    long BaseSize,
    string BaseSha256,
    long TargetSize,
    string TargetSha256,
    string DataPath,
    List<AppFilePatchOperation> Operations);

internal sealed record AppFilePatchOperation(
    string Type,
    long Offset,
    long DataOffset,
    int Length);

internal sealed record UpdateState(
    string Version,
    string TargetPath,
    string Status,
    DateTimeOffset UpdatedAt,
    string? Error);

internal sealed record UpdatePlan(
    List<CopyOperation> Copy,
    List<PatchOperation> Patch,
    List<string> Delete,
    int UnchangedCount);

internal sealed record CopyOperation(
    string Path,
    bool HadExistingFile);

internal sealed record PatchOperation(
    string Path,
    string DataPath,
    bool HadExistingFile,
    AppFilePatchEntry Patch);
