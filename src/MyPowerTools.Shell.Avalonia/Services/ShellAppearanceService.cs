using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Styling;
using MyPowerTools.HostControl;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class ShellAppearanceService
{
    public const string SystemTheme = "system";
    public const string LightTheme = "light";
    public const string DarkTheme = "dark";

    private readonly string _preferencesPath;

    public ShellAppearanceService(string? preferencesPath = null)
    {
        _preferencesPath = preferencesPath ?? DefaultPreferencesPath();
        CurrentTheme = ReadTheme(_preferencesPath);
    }

    public string CurrentTheme { get; private set; }

    public Task SetThemeAsync(string theme)
    {
        CurrentTheme = NormalizeTheme(theme);
        var directory = Path.GetDirectoryName(_preferencesPath)
            ?? throw new InvalidOperationException("The shell preferences path has no parent directory.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            _preferencesPath,
            new JsonObject { ["theme"] = CurrentTheme }.ToJsonString());

        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = ToThemeVariant(CurrentTheme);
        }

        return Task.CompletedTask;
    }

    public static void ApplySavedTheme(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.RequestedThemeVariant = ToThemeVariant(ReadTheme(DefaultPreferencesPath()));
    }

    private static string DefaultPreferencesPath()
    {
        return Path.Combine(
            HostControlAuthTokenStore.DefaultDataRoot(),
            "state",
            "shell-preferences.json");
    }

    private static string ReadTheme(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return SystemTheme;
            }

            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return NormalizeTheme(root?["theme"]?.GetValue<string>());
        }
        catch
        {
            return SystemTheme;
        }
    }

    private static string NormalizeTheme(string? theme)
    {
        return theme?.Trim().ToLowerInvariant() switch
        {
            LightTheme => LightTheme,
            DarkTheme => DarkTheme,
            _ => SystemTheme
        };
    }

    private static ThemeVariant ToThemeVariant(string theme)
    {
        return NormalizeTheme(theme) switch
        {
            LightTheme => ThemeVariant.Light,
            DarkTheme => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
