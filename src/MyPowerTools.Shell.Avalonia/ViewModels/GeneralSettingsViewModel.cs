using System.Windows.Input;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class GeneralSettingsViewModel : ShellPageViewModel
{
    private readonly Func<string, Task> _selectTheme;
    private ThemeChoiceViewModel _selectedTheme;
    private string _themeApplyStatus = "Changes apply immediately.";

    public GeneralSettingsViewModel(
        string selectedTheme,
        Func<string, Task> selectTheme,
        Func<Task> openSystem,
        DevSourceSyncService? devSource = null,
        IReadOnlyList<GlobalHotkeyViewModel>? globalHotkeys = null,
        Func<Task>? openShortcuts = null)
        : base("General", "Application preferences for MyPowerTools.", "ready")
    {
        _selectTheme = selectTheme ?? throw new ArgumentNullException(nameof(selectTheme));
        ArgumentNullException.ThrowIfNull(openSystem);

        Themes =
        [
            new ThemeChoiceViewModel(
                ShellAppearanceService.SystemTheme,
                "Use system setting",
                "Follow the Windows light or dark appearance."),
            new ThemeChoiceViewModel(
                ShellAppearanceService.LightTheme,
                "Light",
                "Keep MyPowerTools in light appearance."),
            new ThemeChoiceViewModel(
                ShellAppearanceService.DarkTheme,
                "Dark",
                "Keep MyPowerTools in dark appearance.")
        ];

        _selectedTheme = Themes.FirstOrDefault(choice =>
            string.Equals(choice.Id, selectedTheme, StringComparison.OrdinalIgnoreCase))
            ?? Themes[0];
        OpenSystemCommand = new AsyncRelayCommand(openSystem);
        OpenShortcutsCommand = new AsyncRelayCommand(openShortcuts ?? (() => Task.CompletedTask));
        DevSource = devSource is null ? null : new DevSourceSettingsViewModel(devSource);
        GlobalHotkeys = globalHotkeys ?? [];
        var conflicts = GlobalHotkeys.Count(hotkey => hotkey.IsConflict);
        GlobalHotkeyStatusText = conflicts > 0
            ? $"{GlobalHotkeys.Count} shortcuts - {conflicts} conflict{(conflicts == 1 ? "" : "s")}"
            : $"{GlobalHotkeys.Count} shortcut{(GlobalHotkeys.Count == 1 ? "" : "s")}";
    }

    /// <summary>
    /// Every registered global hotkey across modules plus the built-in command
    /// palette (PowerToys Keyboard Manager style overview). Editing happens on
    /// each module's settings page.
    /// </summary>
    public IReadOnlyList<GlobalHotkeyViewModel> GlobalHotkeys { get; }
    public bool HasGlobalHotkeys => GlobalHotkeys.Count > 0;
    public string GlobalHotkeyStatusText { get; }

    public IReadOnlyList<ThemeChoiceViewModel> Themes { get; }
    public ICommand OpenSystemCommand { get; }
    public ICommand OpenShortcutsCommand { get; }
    public DevSourceSettingsViewModel? DevSource { get; }
    public string ThemeSummary => SelectedTheme.Description;

    public ThemeChoiceViewModel SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value is null || !SetProperty(ref _selectedTheme, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ThemeSummary));
            _ = ApplyThemeAsync(value);
        }
    }

    public string ThemeApplyStatus
    {
        get => _themeApplyStatus;
        private set => SetProperty(ref _themeApplyStatus, value);
    }

    private async Task ApplyThemeAsync(ThemeChoiceViewModel theme)
    {
        ThemeApplyStatus = "Applying appearance…";
        try
        {
            await _selectTheme(theme.Id);
            ThemeApplyStatus = $"{theme.Title} applied.";
        }
        catch (Exception ex)
        {
            ThemeApplyStatus = $"Could not apply appearance: {ex.Message}";
        }
    }
}

public sealed record ThemeChoiceViewModel(string Id, string Title, string Description);
