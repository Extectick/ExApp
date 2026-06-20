using System.IO.Compression;
using ExApp.Core.Updates;

namespace ExApp.Packaging.Tests;

public sealed class AppPackageExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ExApp.AppExtractorTests.{Guid.NewGuid():N}");

    [Fact]
    public void ExtractSecure_ExtractsSafeArchive()
    {
        Directory.CreateDirectory(_root);
        var packagePath = Path.Combine(_root, "safe.zip");
        CreateZip(packagePath, ("app-files.json", "{}"), ("bin/ExApp.Desktop.exe", "MZ"));
        var destination = Path.Combine(_root, "staging");

        AppPackageExtractor.ExtractSecure(packagePath, destination);

        Assert.Equal("{}", File.ReadAllText(Path.Combine(destination, "app-files.json")));
        Assert.Equal("MZ", File.ReadAllText(Path.Combine(destination, "bin", "ExApp.Desktop.exe")));
    }

    [Fact]
    public void ExtractSecure_RejectsPathTraversalWithoutWritingOutsideStaging()
    {
        Directory.CreateDirectory(_root);
        var packagePath = Path.Combine(_root, "traversal.zip");
        CreateZip(packagePath, ("app-files.json", "{}"), ("../escaped.txt", "escaped"));
        var destination = Path.Combine(_root, "staging");

        var exception = Assert.Throws<InvalidOperationException>(() => AppPackageExtractor.ExtractSecure(packagePath, destination));

        Assert.Contains("not a safe relative path", exception.Message);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void ExtractSecure_RejectsDuplicateEntries()
    {
        Directory.CreateDirectory(_root);
        var packagePath = Path.Combine(_root, "duplicate.zip");
        CreateZip(packagePath, ("app-files.json", "{}"), ("app-files.json", "duplicate"));
        var destination = Path.Combine(_root, "staging");

        var exception = Assert.Throws<InvalidOperationException>(() => AppPackageExtractor.ExtractSecure(packagePath, destination));

        Assert.Contains("duplicated", exception.Message);
        Assert.False(Directory.Exists(destination));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void CreateZip(string path, params (string Path, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            var entry = archive.CreateEntry(entryPath);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }
}
