using System.Text.Json;

namespace MyApp.Packaging;

internal static class AtomicJsonStore
{
    public static T? Read<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), PackageJson.Options);
        }
        catch (JsonException exception)
        {
            throw new PackageException("state.invalidJson", $"State file '{path}' contains invalid JSON.", exception);
        }
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, PackageJson.Options));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
