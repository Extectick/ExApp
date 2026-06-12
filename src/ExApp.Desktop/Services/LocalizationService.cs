using System.Globalization;
using System.Text.Json;

namespace ExApp.Desktop.Services;

public sealed class LocalizationService
{
    public const string SystemLanguage = "system";
    public const string FallbackLanguage = "en";

    private static readonly Lazy<LocalizationService> LazyInstance = new(() => new LocalizationService());
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _languages = new(StringComparer.OrdinalIgnoreCase);

    public static LocalizationService Current => LazyInstance.Value;

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage { get; private set; } = FallbackLanguage;

    public IReadOnlyList<string> AvailableLanguages => _languages.Keys.OrderBy(language => language, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Initialize()
    {
        LoadLanguages();
        CurrentLanguage = ResolveLanguage(AppSettings.LanguagePreference);
    }

    public void SetLanguagePreference(string preference)
    {
        AppSettings.LanguagePreference = preference;
        CurrentLanguage = ResolveLanguage(preference);
        AppLogger.Info($"Language changed to {CurrentLanguage}.");
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Translate(string key)
    {
        if (_languages.TryGetValue(CurrentLanguage, out var current) && current.TryGetValue(key, out var value))
        {
            return value;
        }

        if (_languages.TryGetValue(FallbackLanguage, out var fallback) && fallback.TryGetValue(key, out value))
        {
            return value;
        }

        return key;
    }

    public string GetDisplayName(string language)
    {
        if (string.Equals(language, SystemLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return Translate("settings.language.system");
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            return culture.NativeName;
        }
        catch (CultureNotFoundException)
        {
            return language;
        }
    }

    private void LoadLanguages()
    {
        _languages.Clear();

        var directory = Path.Combine(AppContext.BaseDirectory, "Localization");
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var language = Path.GetFileNameWithoutExtension(file);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
            if (!string.IsNullOrWhiteSpace(language) && values is not null)
            {
                _languages[language] = values;
            }
        }
    }

    private string ResolveLanguage(string preference)
    {
        if (!string.Equals(preference, SystemLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return _languages.ContainsKey(preference) ? preference : FallbackLanguage;
        }

        foreach (var culture in CultureInfo.CurrentUICulture.Parent.Name.Length == 0
            ? new[] { CultureInfo.CurrentUICulture.Name }
            : new[] { CultureInfo.CurrentUICulture.Name, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName })
        {
            if (_languages.ContainsKey(culture))
            {
                return culture;
            }
        }

        return FallbackLanguage;
    }
}
