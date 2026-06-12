using System.Text.Json;

namespace ExApp.Packaging;

internal static class PackageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        WriteIndented = true
    };
}
