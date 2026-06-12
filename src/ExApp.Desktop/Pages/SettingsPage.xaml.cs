using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ExApp.Desktop.Services;

namespace ExApp.Desktop.Pages;

public sealed partial class SettingsPage : Page, ILocalizedPage
{
    private bool _isLoadingLanguageOptions;
    private bool _isLoadingUpdateOptions;

    public SettingsPage()
    {
        InitializeComponent();
        ApplyLocalization();
        SetSelectedTheme();
        LoadLanguageOptions();
        LoadUpdateOptions();
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
        UpdatesTitleText.Text = localization.Translate("settings.updates.title");
        AutomaticUpdateChecksToggle.Header = localization.Translate("settings.updates.automatic");
        StableChannelOption.Content = localization.Translate("settings.updates.channel.stable");
        BetaChannelOption.Content = localization.Translate("settings.updates.channel.beta");
        CheckUpdatesButtonText.Text = localization.Translate("settings.updates.check");
        InstallUpdateButtonText.Text = localization.Translate("settings.updates.install");
        UpdateHistoryTitleText.Text = localization.Translate("settings.updates.history");
        UpdateVersionsText();
        UpdateHistory();

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

    private void LoadUpdateOptions()
    {
        _isLoadingUpdateOptions = true;
        try
        {
            AutomaticUpdateChecksToggle.IsOn = AppSettings.AutomaticUpdateChecks;
            UpdateChannelOptions.SelectedIndex = AppSettings.UpdateChannel == "beta" ? 1 : 0;
            UpdateVersionsText();
            ApplyLastUpdateCheck();
            UpdateHistory();
        }
        finally
        {
            _isLoadingUpdateOptions = false;
        }
    }

    private void UpdateVersionsText()
    {
        if (VersionsText is null)
        {
            return;
        }

        VersionsText.Text = string.Format(
            LocalizationService.Current.Translate("settings.updates.versions"),
            ApplicationUpdateService.Current.CurrentVersion);
    }

    private void AutomaticUpdateChecksToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoadingUpdateOptions)
        {
            AppSettings.AutomaticUpdateChecks = AutomaticUpdateChecksToggle.IsOn;
        }
    }

    private void UpdateChannelOptions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoadingUpdateOptions &&
            UpdateChannelOptions.SelectedItem is ComboBoxItem { Tag: string channel })
        {
            AppSettings.UpdateChannel = channel;
        }
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        SetUpdateBusy(true);
        try
        {
            await ApplicationUpdateService.Current.CheckAsync();
            ApplyLastUpdateCheck();
        }
        catch (Exception exception)
        {
            ShowUpdateMessage(
                InfoBarSeverity.Error,
                LocalizationService.Current.Translate("settings.updates.failed"),
                exception.Message);
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var release = ApplicationUpdateService.Current.LastCheck?.Release;
        if (release is null)
        {
            return;
        }

        SetUpdateBusy(true);
        UpdateProgress.IsIndeterminate = false;
        var progress = new Progress<double>(value => UpdateProgress.Value = value * 100);
        try
        {
            await ApplicationUpdateService.Current.PrepareAndLaunchAsync(release, progress);
            ((App)Application.Current).MainWindow?.ExitApplication();
        }
        catch (Exception exception)
        {
            ShowUpdateMessage(
                InfoBarSeverity.Error,
                LocalizationService.Current.Translate("settings.updates.failed"),
                exception.Message);
            SetUpdateBusy(false);
        }
    }

    private void ApplyLastUpdateCheck()
    {
        var result = ApplicationUpdateService.Current.LastCheck;
        if (result is null)
        {
            return;
        }

        InstallUpdateButton.Visibility = result.IsUpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButtonText.Text = result.Release is null
            ? LocalizationService.Current.Translate("settings.updates.install")
            : string.Format(
                LocalizationService.Current.Translate("settings.updates.installVersion"),
                result.Release.Version);
        ShowUpdateMessage(
            InfoBarSeverity.Success,
            LocalizationService.Current.Translate(result.IsUpdateAvailable
                ? "settings.updates.available"
                : "settings.updates.current"),
            result.IsUpdateAvailable && result.Release is not null
                ? string.Format(
                    LocalizationService.Current.Translate("settings.updates.availableMessage"),
                    result.Release.Version)
                : LocalizationService.Current.Translate("settings.updates.currentMessage"));
    }

    private void SetUpdateBusy(bool isBusy)
    {
        CheckUpdatesButton.IsEnabled = !isBusy;
        InstallUpdateButton.IsEnabled = !isBusy;
        UpdateChannelOptions.IsEnabled = !isBusy;
        UpdateProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        if (isBusy)
        {
            UpdateProgress.IsIndeterminate = true;
        }
    }

    private void ShowUpdateMessage(InfoBarSeverity severity, string title, string message)
    {
        UpdateInfoBar.Severity = severity;
        UpdateInfoBar.Title = title;
        UpdateInfoBar.Message = message;
        UpdateInfoBar.IsOpen = true;
    }

    private void UpdateHistory()
    {
        if (UpdateHistoryText is null)
        {
            return;
        }

        var entries = UpdateHistoryStore.ReadRecent();
        UpdateHistoryText.Text = entries.Count == 0
            ? LocalizationService.Current.Translate("settings.updates.history.empty")
            : string.Join(
                Environment.NewLine,
                entries.Select(entry =>
                    $"{entry.Timestamp:g} · {entry.Component} {entry.Version} · {entry.Status}"));
    }

    private sealed record LanguageOption(string Code, string DisplayName);
}
