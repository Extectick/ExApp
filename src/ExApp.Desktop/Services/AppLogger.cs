namespace ExApp.Desktop.Services;

internal static class AppLogger
{
    private static readonly object SyncRoot = new();

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message, Exception exception)
    {
        Write("ERROR", $"{message}: {exception}");
    }

    private static void Write(string level, string message)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExApp",
                "logs");
            Directory.CreateDirectory(logDirectory);

            var line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
            lock (SyncRoot)
            {
                File.AppendAllText(Path.Combine(logDirectory, "app.log"), line);
            }
        }
        catch
        {
            // Logging must not break app startup or shutdown.
        }
    }
}
