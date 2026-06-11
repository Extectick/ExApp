namespace MyApp.ServiceRuntime;

public sealed class ServiceLogRouter
{
    private const int MaxReturnedLines = 200;

    public string Read(ServiceDescriptor service)
    {
        var files = GetLogFiles(service);
        if (files.Length == 0)
        {
            return string.Empty;
        }

        var lines = files
            .SelectMany(ReadLinesSafe)
            .TakeLast(MaxReturnedLines);
        return string.Join(Environment.NewLine, lines);
    }

    public void Clear(ServiceDescriptor service)
    {
        foreach (var file in GetLogFiles(service))
        {
            TryClear(file);
        }
    }

    private static string[] GetLogFiles(ServiceDescriptor service)
    {
        if (!Directory.Exists(service.RuntimeDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(service.RuntimeDirectory, "*.log", SearchOption.TopDirectoryOnly)
            .OrderBy(File.GetLastWriteTimeUtc)
            .ToArray();
    }

    private static IEnumerable<string> ReadLinesSafe(string file)
    {
        try
        {
            return File.ReadLines(file);
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static void TryClear(string file)
    {
        try
        {
            File.WriteAllText(file, string.Empty);
        }
        catch (IOException)
        {
            // Clearing logs is best-effort; the service can be writing at the same time.
        }
    }
}
