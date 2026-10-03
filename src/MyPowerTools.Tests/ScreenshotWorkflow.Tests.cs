using Screenshot.MyPowerTools;

namespace MyPowerTools.Tests;

public sealed class ScreenshotWorkflowTests
{
    [Fact]
    public void Switching_owned_backend_stops_old_install_and_starts_selected_install()
    {
        var environment = new EnvironmentStub();
        var session = new ScreenshotSession(environment);
        Assert.True(session.Open(environment.First).Success);
        var firstPid = environment.Starts.Single().Id;

        var selected = session.Open(environment.Second);

        Assert.True(selected.Success);
        Assert.Equal(2, environment.Starts.Count);
        Assert.Equal(environment.Second, environment.Starts.Last().Path);
        Assert.Equal(new[] { firstPid }, environment.Killed);
        Assert.True(selected.Inspection.OwnedByModule);
        session.StopOwned();
        Assert.Equal(2, environment.Killed.Count);
    }

    [Fact]
    public void Inspecting_another_install_does_not_attribute_old_owned_process_to_it()
    {
        var environment = new EnvironmentStub();
        var session = new ScreenshotSession(environment);
        session.Open(environment.First);
        var inspection = session.Inspect(environment.Second);
        Assert.False(inspection.OwnedByModule);
        Assert.False(inspection.Running);
        session.StopOwned();
        Assert.Single(environment.Killed);
    }

    [Fact]
    public void Failed_backend_switch_retains_previous_process_for_safe_cleanup()
    {
        var environment = new EnvironmentStub();
        var session = new ScreenshotSession(environment);
        session.Open(environment.First);
        var result = session.Open(Path.Combine(Path.GetTempPath(), "missing", "snow_shot.exe"));
        Assert.False(result.Success);
        Assert.Single(environment.Starts);
        Assert.Empty(environment.Killed);
        session.StopOwned();
        Assert.Single(environment.Killed);
    }

    [Fact]
    public void Capture_failure_reports_injection_error_and_owned_resident_remains_cleanupable()
    {
        var environment = new EnvironmentStub { InjectionSucceeds = false };
        var session = new ScreenshotSession(environment);
        var result = session.Capture(environment.First);
        Assert.False(result.Success);
        Assert.Contains("synthetic injection refused", result.Message);
        Assert.Equal(new[] { "Ctrl+Shift+S" }, environment.Shortcuts);
        session.StopOwned();
        Assert.Single(environment.Killed);
    }

    [Fact]
    public void Launch_failure_returns_structured_failure_and_can_retry()
    {
        var environment = new EnvironmentStub { FailStart = true };
        var session = new ScreenshotSession(environment);
        Assert.False(session.Open(environment.First).Success);
        Assert.False(session.Inspect(environment.First).OwnedByModule);
        environment.FailStart = false;
        Assert.True(session.Open(environment.First).Success);
        session.StopOwned();
        Assert.Single(environment.Killed);
    }

    private sealed class EnvironmentStub : IScreenshotEnvironment
    {
        public void UseKeyboardShortcuts(MyPowerTools.Platform.Abstractions.IKeyboardShortcutService? service) { }
        public string First { get; } = Path.Combine(Path.GetTempPath(), "mpt-e2e-screenshot-old", "snowshot.exe");
        public string Second { get; } = Path.Combine(Path.GetTempPath(), "mpt-e2e-screenshot-new", "snow_shot.exe");
        public ScreenshotPlatform Platform => ScreenshotPlatform.Windows;
        public string? ProgramFiles => null;
        public string? ProgramFilesX86 => null;
        public string? LocalAppData => null;
        public string? UserProfile => null;
        public string? PathEnvironment => null;
        public List<(int Id, string Path)> Starts { get; } = [];
        public List<int> Killed { get; } = [];
        public List<string> Shortcuts { get; } = [];
        public bool InjectionSucceeds { get; set; } = true;
        public bool FailStart { get; set; }
        public bool FileExists(string path) => path == First || path == Second;
        public string? ReadAllText(string path) => null;
        public IEnumerable<string> EnumerateDirectories(string directory) => [];
        public IReadOnlyList<string> WindowsInstallLocations() => [];
        public string? ReadLaunchHelp(string executablePath) => null;
        public string? ReadConfiguredScreenshotHotkey(string executablePath) => "Ctrl+Shift+S";
        public bool TrySendShortcut(string gesture, out string error)
        {
            Shortcuts.Add(gesture);
            error = InjectionSucceeds ? "" : "synthetic injection refused";
            return InjectionSucceeds;
        }
        public void WaitAfterResidentStart() { }
        public bool IsProcessRunning(string processName) => Starts.Any(p => !Killed.Contains(p.Id) && Path.GetFileNameWithoutExtension(p.Path) == processName);
        public bool IsProcessAlive(int processId) => Starts.Any(p => p.Id == processId) && !Killed.Contains(processId);
        public bool TryGetLiveProcessIdentity(int processId, out string processName, out string? imagePath)
        {
            var process = Starts.Find(p => p.Id == processId);
            imagePath = process.Path;
            processName = imagePath is null ? "" : Path.GetFileNameWithoutExtension(imagePath);
            return IsProcessAlive(processId);
        }
        public int StartProcess(string executablePath, IReadOnlyList<string> arguments)
        {
            if (FailStart) throw new InvalidOperationException("synthetic launch refused");
            var id = Starts.Count + 100;
            Starts.Add((id, executablePath));
            return id;
        }
        public void KillProcess(int processId) => Killed.Add(processId);
    }
}
