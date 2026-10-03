using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using Screenshot.MyPowerTools;

namespace MyPowerTools.Tests;

public sealed class ScreenshotProductTests
{
    [Fact]
    public void Windows_resolver_finds_snowshot_and_does_not_invent_a_capture_command()
    {
        var programFiles = @"C:\Program Files";
        var executable = Path.Combine(programFiles, "Snow Shot", "snowshot.exe");
        var environment = new FakeScreenshotEnvironment
        {
            Platform = ScreenshotPlatform.Windows,
            ProgramFiles = programFiles
        };
        environment.Files.Add(executable);

        var backend = ScreenshotBackendResolver.Resolve(environment, null);

        Assert.True(backend.Installed);
        Assert.Equal("snow-shot", backend.BackendId);
        Assert.Equal(executable, backend.ExecutablePath);
        Assert.Empty(backend.CaptureArguments);
        Assert.Contains("不会再占用", backend.HotkeyNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_resolver_reports_winget_when_snowshot_is_missing()
    {
        var backend = ScreenshotBackendResolver.Resolve(new FakeScreenshotEnvironment
        {
            Platform = ScreenshotPlatform.Windows,
            ProgramFiles = @"C:\Program Files"
        }, null);

        Assert.False(backend.Installed);
        Assert.Contains("winget install --id mg-chao.snow-shot", backend.InstallHint, StringComparison.Ordinal);
        Assert.Contains("fixedRuntime", backend.InstallHint, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_path_wins_over_a_discovered_install()
    {
        var programFiles = @"C:\Program Files";
        var discovered = Path.Combine(programFiles, "Snow Shot", "snowshot.exe");
        var portable = @"D:\Tools\snowshot.exe";
        var environment = new FakeScreenshotEnvironment
        {
            Platform = ScreenshotPlatform.Windows,
            ProgramFiles = programFiles
        };
        environment.Files.Add(discovered);
        environment.Files.Add(portable);

        var backend = ScreenshotBackendResolver.Resolve(environment, portable);

        Assert.Equal(portable, backend.ExecutablePath);
    }

    [Fact]
    public void Windows_resolver_finds_a_winget_package_layout()
    {
        var localAppData = @"C:\Users\me\AppData\Local";
        var packages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        var package = Path.Combine(packages, "mg-chao.snow-shot_8wekyb3d8bbwe");
        var executable = Path.Combine(package, "bin", "snow_shot.exe");
        var environment = new FakeScreenshotEnvironment
        {
            Platform = ScreenshotPlatform.Windows,
            LocalAppData = localAppData
        };
        environment.Directories[packages] = [package];
        environment.Files.Add(executable);

        var backend = ScreenshotBackendResolver.Resolve(environment, null);

        Assert.Equal(executable, backend.ExecutablePath);
    }

    [Fact]
    public void Windows_resolver_prefers_current_portable_layout()
    {
        var programFiles = @"C:\Program Files";
        var directory = Path.Combine(programFiles, "Snow Shot");
        var current = Path.Combine(directory, "bin", "snow_shot.exe");
        var legacy = Path.Combine(directory, "snowshot.exe");
        var environment = new FakeScreenshotEnvironment
        {
            Platform = ScreenshotPlatform.Windows,
            ProgramFiles = programFiles
        };
        environment.Files.Add(current);
        environment.Files.Add(legacy);

        var backend = ScreenshotBackendResolver.Resolve(environment, null);

        Assert.Equal(current, backend.ExecutablePath);
        Assert.Equal("snow_shot", backend.ProcessName);
    }

    [Fact]
    public void Mac_resolver_finds_the_snow_shot_bundle()
    {
        var executable = Path.Combine("/Applications", "Snow Shot.app", "Contents", "MacOS", "snowshot");
        var environment = new FakeScreenshotEnvironment { Platform = ScreenshotPlatform.MacOs };
        environment.Files.Add(executable);

        var backend = ScreenshotBackendResolver.Resolve(environment, null);

        Assert.Equal("snow-shot", backend.BackendId);
        Assert.Equal(executable, backend.ExecutablePath);
    }

    [Fact]
    public void Linux_resolver_uses_flameshot_gui()
    {
        var environment = new FakeScreenshotEnvironment
        {
            Platform = ScreenshotPlatform.Linux,
            PathEnvironment = "/usr/bin" + Path.PathSeparator + "/bin"
        };
        environment.Files.Add("/usr/bin/flameshot");

        var backend = ScreenshotBackendResolver.Resolve(environment, null);

        Assert.Equal("flameshot", backend.BackendId);
        Assert.Equal("/usr/bin/flameshot", backend.ExecutablePath);
        Assert.Equal(["gui"], backend.CaptureArguments);
    }

    [Fact]
    public void Help_text_can_add_a_capture_argument_for_snow_shot()
    {
        var executable = @"C:\Program Files\Snow Shot\snowshot.exe";
        var environment = new FakeScreenshotEnvironment
        {
            Platform = ScreenshotPlatform.Windows,
            ProgramFiles = @"C:\Program Files",
            LaunchHelp = "Usage\n  --capture   start a capture\n"
        };
        environment.Files.Add(executable);

        var backend = ScreenshotBackendResolver.Resolve(environment, null);

        Assert.Equal(["--capture"], backend.CaptureArguments);
    }

    [Fact]
    public void Resident_start_runs_once_and_does_not_stop_an_existing_process()
    {
        var environment = WindowsEnvironment();
        environment.RunningNames.Add("snowshot");
        var session = new ScreenshotSession(environment);

        var first = session.EnsureResident(null);
        var second = session.EnsureResident(null);
        session.StopOwned();

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Empty(environment.Starts);
        Assert.Empty(environment.Killed);
    }

    [Fact]
    public void Owned_resident_is_started_once_and_stopped_with_the_module()
    {
        var environment = WindowsEnvironment();
        var session = new ScreenshotSession(environment);

        Assert.True(session.EnsureResident(null).Success);
        Assert.True(session.EnsureResident(null).Success);
        session.StopOwned();

        Assert.Single(environment.Starts);
        Assert.Empty(environment.Starts[0].Arguments);
        Assert.Equal([environment.Starts[0].ProcessId], environment.Killed);
    }

    [Fact]
    public void StopOwned_does_not_kill_a_reused_pid_with_a_different_process_name()
    {
        var environment = WindowsEnvironment();
        var session = new ScreenshotSession(environment);

        Assert.True(session.EnsureResident(null).Success);
        var ownedProcessId = Assert.Single(environment.Starts).ProcessId;
        environment.ReplaceAliveProcess(ownedProcessId, "notepad", @"C:\Windows\System32\notepad.exe");
        session.StopOwned();

        Assert.Empty(environment.Killed);
        Assert.True(environment.IsProcessAlive(ownedProcessId));
    }

    [Fact]
    public void Flameshot_capture_opens_gui_without_taking_ownership()
    {
        var environment = new FakeScreenshotEnvironment
        {
            Platform = ScreenshotPlatform.Linux,
            PathEnvironment = "/usr/bin"
        };
        environment.Files.Add("/usr/bin/flameshot");
        var session = new ScreenshotSession(environment);

        var capture = session.Capture(null);
        session.StopOwned();

        Assert.True(capture.Success);
        Assert.Equal(["gui"], Assert.Single(environment.Starts).Arguments);
        Assert.Empty(environment.Killed);
    }

    [Fact]
    public void Snow_shot_capture_sends_the_configured_hotkey_without_a_second_instance_arg()
    {
        var environment = WindowsEnvironment();
        environment.RunningNames.Add("snowshot");
        environment.ConfiguredHotkey = "Alt+A";
        environment.TextFiles[Path.Combine(@"C:\Program Files", "Snow Shot", "portable", "config.json")] =
            """{"global_shortcuts":{"screenshot":[{"portable":"Alt+A"}]}}""";
        var session = new ScreenshotSession(environment);

        var capture = session.Capture(null);

        Assert.True(capture.Success);
        Assert.Contains("Alt+A", capture.Message, StringComparison.Ordinal);
        Assert.Equal(["Alt+A"], environment.SentShortcuts);
        Assert.Empty(environment.Starts);
    }

    [Fact]
    public void Snow_shot_capture_does_not_send_the_hotkey_on_the_caller_thread()
    {
        var environment = WindowsEnvironment();
        environment.RunningNames.Add("snowshot");
        environment.ConfiguredHotkey = "Alt+A";
        var session = new ScreenshotSession(environment);
        var callerThreadId = Environment.CurrentManagedThreadId;

        var capture = session.Capture(null);

        Assert.True(capture.Success);
        Assert.NotNull(environment.LastSendThreadId);
        Assert.NotEqual(callerThreadId, environment.LastSendThreadId);
        Assert.Equal(["Alt+A"], environment.SentShortcuts);
    }

    [Fact]
    public void Snow_shot_capture_starts_resident_then_sends_hotkey()
    {
        var environment = WindowsEnvironment();
        environment.ConfiguredHotkey = "F1";
        var session = new ScreenshotSession(environment);

        var capture = session.Capture(null);

        Assert.True(capture.Success);
        Assert.Contains("F1", capture.Message, StringComparison.Ordinal);
        Assert.Empty(Assert.Single(environment.Starts).Arguments);
        Assert.Equal(["F1"], environment.SentShortcuts);
    }

    [Fact]
    public void Snow_shot_capture_fails_clearly_when_no_hotkey_is_configured()
    {
        var environment = WindowsEnvironment();
        environment.RunningNames.Add("snowshot");
        environment.ConfiguredHotkey = null;
        var session = new ScreenshotSession(environment);

        var capture = session.Capture(null);

        Assert.False(capture.Success);
        Assert.Contains("没有配置截图快捷键", capture.Message, StringComparison.Ordinal);
        Assert.Empty(environment.Starts);
        Assert.Empty(environment.SentShortcuts);
    }

    [Fact]
    public void Snow_shot_hotkey_parser_reads_portable_config_entries()
    {
        var gesture = ScreenshotSnowShotHotkey.ReadFromConfigJson(
            """{"global_shortcuts":{"screenshot":[{"portable":"Ctrl+Shift+X"}]}}""");

        Assert.Equal("Ctrl+Shift+X", gesture);
    }

    [Fact]
    public async Task Disabled_launch_setting_cancels_the_pending_start()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var environment = WindowsEnvironment();
        var module = new ScreenshotModule(
            new ScreenshotSession(environment),
            (_, cancellationToken) => gate.Task.WaitAsync(cancellationToken));

        await module.StartAsync(Context(), CancellationToken.None);
        await module.ApplySettingsAsync(Settings(launch: false), CancellationToken.None);
        gate.TrySetResult();
        await module.StartupTask;

        Assert.Empty(environment.Starts);
    }

    [Fact]
    public async Task Runner_load_launches_once_when_settings_do_not_arrive()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var environment = WindowsEnvironment();
        var module = new ScreenshotModule(
            new ScreenshotSession(environment),
            (_, _) => gate.Task);

        await module.InitializeAsync(Context(), CancellationToken.None);
        Assert.Empty(environment.Starts);

        gate.TrySetResult();
        await module.StartupTask;

        Assert.Single(environment.Starts);
    }

    [Fact]
    public async Task Startup_launches_once_when_settings_do_not_arrive()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var environment = WindowsEnvironment();
        var module = new ScreenshotModule(
            new ScreenshotSession(environment),
            (_, _) => gate.Task);

        await module.StartAsync(Context(), CancellationToken.None);
        Assert.Empty(environment.Starts);

        gate.TrySetResult();
        await module.StartupTask;

        Assert.Single(environment.Starts);
    }

    [Fact]
    public async Task Applied_launch_setting_starts_immediately_and_the_delayed_start_does_not_repeat()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var environment = WindowsEnvironment();
        var module = new ScreenshotModule(
            new ScreenshotSession(environment),
            (_, _) => gate.Task);

        await module.StartAsync(Context(), CancellationToken.None);
        await module.ApplySettingsAsync(Settings(launch: true), CancellationToken.None);
        gate.TrySetResult();
        await module.StartupTask;

        Assert.Single(environment.Starts);
    }

    [Fact]
    public void Screenshot_manifest_does_not_register_a_host_hotkey()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "screenshot",
            "current-integration",
            "modules",
            "screenshot",
            "module.json")))!.AsObject();
        var commands = JsonNode.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "screenshot",
            "current-integration",
            "modules",
            "screenshot",
            "commands.index.json")))!.AsObject();

        Assert.Empty(manifest["hotkeys"]!.AsArray());
        Assert.Equal(
            ["screenshot.capture", "screenshot.install", "screenshot.open", "screenshot.status"],
            commands["commands"]!.AsArray()
                .Select(command => command!["id"]!.GetValue<string>())
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray());
        Assert.Contains("linux-x64", manifest["entrypoints"]![0]!["platforms"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Contains("macos-arm64", manifest["entrypoints"]![0]!["platforms"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    private static FakeScreenshotEnvironment WindowsEnvironment()
    {
        var programFiles = @"C:\Program Files";
        var environment = new FakeScreenshotEnvironment
        {
            Platform = ScreenshotPlatform.Windows,
            ProgramFiles = programFiles
        };
        environment.Files.Add(Path.Combine(programFiles, "Snow Shot", "snowshot.exe"));
        return environment;
    }

    private static ModuleContext Context()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-screenshot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new ModuleContext("test", "1.0", "screenshot", "screenshot", root, root, root, "windows-x64", []);
    }

    private static SettingsSnapshotDocument Settings(bool launch)
    {
        return new SettingsSnapshotDocument(
            "screenshot",
            1,
            new JsonObject
            {
                ["executablePath"] = "",
                ["launchWithMyPowerTools"] = launch
            },
            DateTimeOffset.UtcNow);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MyPowerTools.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the MyPowerTools repository root.");
    }

    private sealed class FakeScreenshotEnvironment : IScreenshotEnvironment
    {
        public void UseKeyboardShortcuts(MyPowerTools.Platform.Abstractions.IKeyboardShortcutService? service) { }
        private int _nextProcessId = 10;
        private readonly Dictionary<int, string> _processNames = [];
        private readonly Dictionary<int, string?> _processImagePaths = [];

        public ScreenshotPlatform Platform { get; init; }
        public string? ProgramFiles { get; init; }
        public string? ProgramFilesX86 { get; init; }
        public string? LocalAppData { get; init; }
        public string? UserProfile { get; init; }
        public string? PathEnvironment { get; init; }
        public string? LaunchHelp { get; init; }
        public string? ConfiguredHotkey { get; set; } = "Alt+A";
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> TextFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RunningNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<StartedProcess> Starts { get; } = [];
        public List<string> SentShortcuts { get; } = [];
        public int? LastSendThreadId { get; private set; }
        public List<int> Killed { get; } = [];
        private readonly HashSet<int> _alive = [];

        public bool FileExists(string path) => Files.Contains(path);

        public string? ReadAllText(string path)
        {
            return TextFiles.TryGetValue(path, out var text) ? text : null;
        }

        public IEnumerable<string> EnumerateDirectories(string directory)
        {
            return Directories.TryGetValue(directory, out var children) ? children : [];
        }

        public IReadOnlyList<string> WindowsInstallLocations() => [];

        public string? ReadLaunchHelp(string executablePath) => LaunchHelp;

        public string? ReadConfiguredScreenshotHotkey(string executablePath)
        {
            if (ConfiguredHotkey is not null)
            {
                return ConfiguredHotkey;
            }

            foreach (var path in ScreenshotSnowShotHotkey.CandidateConfigPaths(executablePath))
            {
                var gesture = ScreenshotSnowShotHotkey.ReadFromConfigJson(ReadAllText(path));
                if (!string.IsNullOrWhiteSpace(gesture))
                {
                    return gesture;
                }
            }

            return null;
        }

        public bool TrySendShortcut(string gesture, out string error)
        {
            LastSendThreadId = Environment.CurrentManagedThreadId;
            SentShortcuts.Add(gesture);
            error = "";
            return true;
        }

        public void WaitAfterResidentStart()
        {
        }

        public bool IsProcessRunning(string processName) => RunningNames.Contains(processName);

        public bool IsProcessAlive(int processId) => _alive.Contains(processId);

        public bool TryGetLiveProcessIdentity(int processId, out string processName, out string? imagePath)
        {
            if (_alive.Contains(processId) &&
                _processNames.TryGetValue(processId, out var name) &&
                !string.IsNullOrWhiteSpace(name))
            {
                processName = name;
                imagePath = _processImagePaths.TryGetValue(processId, out var path) ? path : null;
                return true;
            }

            processName = "";
            imagePath = null;
            return false;
        }

        public int StartProcess(string executablePath, IReadOnlyList<string> arguments)
        {
            var processId = _nextProcessId++;
            var processName = Path.GetFileNameWithoutExtension(executablePath);
            Starts.Add(new StartedProcess(processId, executablePath, arguments));
            _alive.Add(processId);
            _processNames[processId] = processName;
            _processImagePaths[processId] = executablePath;
            RunningNames.Add(processName);
            return processId;
        }

        public void ReplaceAliveProcess(int processId, string processName, string? imagePath)
        {
            if (!_alive.Contains(processId))
            {
                throw new InvalidOperationException($"Process {processId} is not alive.");
            }

            if (_processNames.TryGetValue(processId, out var previousName) &&
                !_processNames.Any(pair => pair.Key != processId &&
                    string.Equals(pair.Value, previousName, StringComparison.OrdinalIgnoreCase)))
            {
                RunningNames.Remove(previousName);
            }

            _processNames[processId] = processName;
            _processImagePaths[processId] = imagePath;
            RunningNames.Add(processName);
        }

        public void KillProcess(int processId)
        {
            Killed.Add(processId);
            _alive.Remove(processId);
            _processImagePaths.Remove(processId);
            if (_processNames.Remove(processId, out var processName) &&
                !_processNames.ContainsValue(processName))
            {
                RunningNames.Remove(processName);
            }
        }
    }

    private sealed record StartedProcess(int ProcessId, string ExecutablePath, IReadOnlyList<string> Arguments);
}
