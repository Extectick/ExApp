using Microsoft.UI.Xaml;

namespace ExApp.Desktop.Services;

public enum AppThemePreference
{
    System,
    Light,
    Dark
}

public static class AppSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExApp",
        "settings");

    private static readonly string ThemePath = Path.Combine(SettingsDirectory, "theme.txt");
    private static readonly string LanguagePath = Path.Combine(SettingsDirectory, "language.txt");
    private static readonly string UpdateChannelPath = Path.Combine(SettingsDirectory, "update-channel.txt");
    private static readonly string AutomaticUpdateChecksPath = Path.Combine(SettingsDirectory, "automatic-update-checks.txt");

    public static AppThemePreference ThemePreference
    {
        get
        {
            var value = File.Exists(ThemePath) ? File.ReadAllText(ThemePath).Trim() : null;
            return Enum.TryParse<AppThemePreference>(value, out var theme) ? theme : AppThemePreference.System;
        }
        set
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(ThemePath, value.ToString());
        }
    }

    public static string LanguagePreference
    {
        get
        {
            var value = File.Exists(LanguagePath) ? File.ReadAllText(LanguagePath).Trim() : null;
            return string.IsNullOrWhiteSpace(value) ? LocalizationService.SystemLanguage : value;
        }
        set
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(LanguagePath, string.IsNullOrWhiteSpace(value) ? LocalizationService.SystemLanguage : value);
        }
    }

    public static string UpdateChannel
    {
        get
        {
            var value = File.Exists(UpdateChannelPath) ? File.ReadAllText(UpdateChannelPath).Trim() : null;
            return value is "beta" ? value : "stable";
        }
        set
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(UpdateChannelPath, value is "beta" ? value : "stable");
        }
    }

    public static bool AutomaticUpdateChecks
    {
        get
        {
            var value = File.Exists(AutomaticUpdateChecksPath)
                ? File.ReadAllText(AutomaticUpdateChecksPath).Trim()
                : null;
            return !bool.TryParse(value, out var enabled) || enabled;
        }
        set
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(AutomaticUpdateChecksPath, value.ToString());
        }
    }

    public static ElementTheme ToElementTheme(AppThemePreference preference) =>
        preference switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
}
