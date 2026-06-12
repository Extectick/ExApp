using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ExApp.Desktop.Services;

internal static class ServiceIconResolver
{
    private const string GlyphPrefix = "glyph:";
    private static readonly Dictionary<string, ServiceIconAsset> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static ServiceIconAsset Resolve(string serviceId, string? icon) =>
        Resolve(string.IsNullOrWhiteSpace(icon) && serviceId.Equals("mock-service", StringComparison.OrdinalIgnoreCase)
            ? "glyph:E713"
            : icon);

    public static ServiceIconAsset Resolve(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return ServiceIconAsset.Empty;
        }

        var normalizedIcon = icon.Trim();
        if (IconCache.TryGetValue(normalizedIcon, out var cached))
        {
            return cached;
        }

        if (normalizedIcon.StartsWith(GlyphPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var glyph = ResolveGlyph(normalizedIcon[GlyphPrefix.Length..]);
            var asset = string.IsNullOrEmpty(glyph)
                ? ServiceIconAsset.Empty
                : new ServiceIconAsset(null, glyph);
            IconCache[normalizedIcon] = asset;
            return asset;
        }

        if (Uri.TryCreate(normalizedIcon, UriKind.Absolute, out var absoluteUri))
        {
            var asset = new ServiceIconAsset(new BitmapImage(absoluteUri), null);
            IconCache[normalizedIcon] = asset;
            return asset;
        }

        var path = ResolveLocalPath(normalizedIcon);
        var resolvedAsset = File.Exists(path)
            ? new ServiceIconAsset(new BitmapImage(new Uri(path)), null)
            : ServiceIconAsset.Empty;
        IconCache[normalizedIcon] = resolvedAsset;
        return resolvedAsset;
    }

    public static bool IsGlyphIcon(string? icon) =>
        !string.IsNullOrWhiteSpace(icon) &&
        !string.IsNullOrEmpty(ResolveGlyph(icon.StartsWith(GlyphPrefix, StringComparison.OrdinalIgnoreCase)
            ? icon[GlyphPrefix.Length..]
            : string.Empty));

    private static string? ResolveGlyph(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 4 or > 6 ||
            !normalized.All(character => Uri.IsHexDigit(character)) ||
            !int.TryParse(normalized, System.Globalization.NumberStyles.HexNumber, null, out var codePoint))
        {
            return null;
        }

        try
        {
            return char.ConvertFromUtf32(codePoint);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string ResolveLocalPath(string path)
    {
        var normalizedPath = path.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(normalizedPath))
        {
            return normalizedPath;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.GetFullPath(Path.Combine(current.FullName, normalizedPath));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.GetFullPath(Path.Combine(current.FullName, "catalog", normalizedPath));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, normalizedPath));
    }
}

internal sealed record ServiceIconAsset(ImageSource? ImageSource, string? Glyph)
{
    public static ServiceIconAsset Empty { get; } = new(null, null);
}
