using System.Text.Json;

namespace ExApp.Desktop.Services;

internal sealed record UpdateHistoryEntry(
    DateTimeOffset Timestamp,
    string Component,
    string Version,
    string Status,
    string? Message);

internal static class UpdateHistoryStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExApp",
        "updates",
        "history.jsonl");

    public static async Task AddAsync(UpdateHistoryEntry entry)
    {
        await Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            await File.AppendAllTextAsync(
                HistoryPath,
                JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static IReadOnlyList<UpdateHistoryEntry> ReadRecent(int count = 5)
    {
        if (!File.Exists(HistoryPath))
        {
            return [];
        }

        return File.ReadLines(HistoryPath)
            .Reverse()
            .Select(line =>
            {
                try
                {
                    return JsonSerializer.Deserialize<UpdateHistoryEntry>(line);
                }
                catch (JsonException)
                {
                    return null;
                }
            })
            .Where(entry => entry is not null)
            .Take(count)
            .Cast<UpdateHistoryEntry>()
            .ToArray();
    }
}
