using System.Diagnostics;
using System.Windows.Input;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class ExternalSdkToolViewModel : ShellPageViewModel
{
    private string _surfaceState = "loading";
    private string _surfaceMessage = "Connecting to the tool surface.";

    public ExternalSdkToolViewModel(
        string toolId,
        string title,
        string subtitle,
        string toolType,
        string routeTitle,
        Uri? source,
        bool openExternal,
        IReadOnlyList<ExternalToolCommandViewModel> commands,
        string? settingsPath,
        Func<string, Task<string>> handleBridgeRequest,
        Func<Task> refresh,
        Func<Task> returnToTools,
        Func<Task>? launch = null)
        : base(title, subtitle)
    {
        ToolId = toolId;
        ToolType = toolType;
        RouteTitle = routeTitle;
        Source = source;
        CanOpenExternal = openExternal && source is not null;
        Commands = commands;
        SettingsPath = settingsPath;
        HandleBridgeRequestAsync = handleBridgeRequest;
        RefreshCommand = new AsyncRelayCommand(refresh);
        ReturnToToolsCommand = new AsyncRelayCommand(returnToTools);
        OpenExternalCommand = new AsyncRelayCommand(OpenExternalAsync, () => CanOpenExternal);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync, () => CanOpenSettings);
        LaunchCommand = new AsyncRelayCommand(launch ?? (() => Task.CompletedTask), () => launch is not null);
    }

    public string ToolId { get; }
    public string ToolType { get; }
    public string RouteTitle { get; }
    public Uri? Source { get; }
    public bool IsWeb => ToolType == "web-surface";
    public bool IsDotnet => ToolType == "dotnet-surface";
    public bool IsNative => ToolType == "native-tool";
    public bool IsHeadless => ToolType == "headless-tool";
    public bool IsGeneric => IsNative || IsHeadless;
    public bool CanOpenExternal { get; }
    public IReadOnlyList<ExternalToolCommandViewModel> Commands { get; }
    public bool HasCommands => Commands.Count > 0;
    public string? SettingsPath { get; }
    public bool CanOpenSettings => !string.IsNullOrWhiteSpace(SettingsPath) && File.Exists(SettingsPath);
    public ICommand RefreshCommand { get; }
    public ICommand ReturnToToolsCommand { get; }
    public ICommand OpenExternalCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand LaunchCommand { get; }
    public Func<string, Task<string>> HandleBridgeRequestAsync { get; }

    public string SurfaceState
    {
        get => _surfaceState;
        private set
        {
            if (SetProperty(ref _surfaceState, value))
            {
                OnPropertyChanged(nameof(IsSurfaceLoading));
                OnPropertyChanged(nameof(IsSurfaceFailed));
                OnPropertyChanged(nameof(IsSurfaceReady));
            }
        }
    }

    public string SurfaceMessage
    {
        get => _surfaceMessage;
        private set => SetProperty(ref _surfaceMessage, value);
    }

    public bool IsSurfaceLoading => SurfaceState == "loading";
    public bool IsSurfaceFailed => SurfaceState is "failed" or "unavailable";
    public bool IsSurfaceReady => SurfaceState == "ready";

    public void ReportSurface(string state, string message = "")
    {
        SurfaceState = state;
        SurfaceMessage = string.IsNullOrWhiteSpace(message)
            ? state == "ready" ? "Connected." : "The tool surface is unavailable."
            : message;
    }

    private Task OpenExternalAsync()
    {
        if (Source is not null)
        {
            if (Source.Scheme is not ("http" or "https") && !(Source.IsFile && File.Exists(Source.LocalPath)))
            {
                throw new InvalidOperationException("The configured external target is invalid.");
            }
            Process.Start(new ProcessStartInfo(Source.AbsoluteUri) { UseShellExecute = true });
        }
        return Task.CompletedTask;
    }

    private Task OpenSettingsAsync()
    {
        if (CanOpenSettings)
        {
            Process.Start(new ProcessStartInfo(SettingsPath!) { UseShellExecute = true });
        }
        return Task.CompletedTask;
    }
}

public sealed class ExternalToolCommandViewModel
{
    public ExternalToolCommandViewModel(string title, string description, Func<Task> execute)
    {
        Title = title;
        Description = description;
        ExecuteCommand = new AsyncRelayCommand(execute);
    }

    public string Title { get; }
    public string Description { get; }
    public ICommand ExecuteCommand { get; }
}
