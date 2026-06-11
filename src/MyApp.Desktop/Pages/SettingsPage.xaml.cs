using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MyApp.Desktop.Services;

namespace MyApp.Desktop.Pages;

public sealed partial class SettingsPage : Page, ILocalizedPage
{
    private bool _isLoadingLanguageOptions;

    public SettingsPage()
    {
        InitializeComponent();
        ApplyLocalization();
        SetSelectedTheme();
        LoadLanguageOptions();
    }

    public void ApplyLocalization()
    {
        var localization = LocalizationService.Current;
        TitleText.Text = localization.Translate("settings.title");
        ThemeTitleText.Text = localization.Translate("settings.theme.title");
        ThemeSystemOption.Content = localization.Translate("settings.theme.system");
        ThemeLightOption.Content = localization.Translate("settings.theme.light");
        ThemeDarkOption.Content = localization.Translate("settings.theme.dark");
        LanguageTitleText.Text = localization.Translate("settings.language.title");

        if (LanguageOptions.Items.Count > 0)
        {
            LoadLanguageOptions();
        }
    }

    private void SetSelectedTheme()
    {
        var current = AppSettings.ThemePreference.ToString();
        foreach (var item in ThemeOptions.Items)
        {
            if (item is RadioButton button && string.Equals(button.Tag as string, current, StringComparison.Ordinal))
            {
                ThemeOptions.SelectedItem = button;
                break;
            }
        }
    }

    private void ThemeOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeOptions.SelectedItem is not RadioButton button ||
            button.Tag is not string tag ||
            !Enum.TryParse<AppThemePreference>(tag, out var preference))
        {
            return;
        }

        AppSettings.ThemePreference = preference;
        ((App)Application.Current).ApplyTheme(preference);
    }

    private void LoadLanguageOptions()
    {
        _isLoadingLanguageOptions = true;
        try
        {
            var localization = LocalizationService.Current;
            var selectedPreference = AppSettings.LanguagePreference;
            var options = new List<LanguageOption>
            {
                new(LocalizationService.SystemLanguage, localization.GetDisplayName(LocalizationService.SystemLanguage))
            };

            options.AddRange(localization.AvailableLanguages.Select(language => new LanguageOption(language, localization.GetDisplayName(language))));
            LanguageOptions.ItemsSource = options;
            LanguageOptions.SelectedItem = options.FirstOrDefault(option => string.Equals(option.Code, selectedPreference, StringComparison.OrdinalIgnoreCase))
                ?? options[0];
        }
        finally
        {
            _isLoadingLanguageOptions = false;
        }
    }

    private void LanguageOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingLanguageOptions || LanguageOptions.SelectedItem is not LanguageOption option)
        {
            return;
        }

        LocalizationService.Current.SetLanguagePreference(option.Code);
    }

    private sealed record LanguageOption(string Code, string DisplayName);
}
