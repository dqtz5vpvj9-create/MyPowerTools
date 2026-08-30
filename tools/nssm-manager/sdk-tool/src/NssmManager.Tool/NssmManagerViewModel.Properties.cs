using System.Collections.ObjectModel;
using MyPowerTools.AvaloniaSdk;
using NssmManager.Contracts;

namespace NssmManager.Tool;

public sealed partial class NssmManagerViewModel
{
    public ObservableCollection<NssmServiceSnapshot> Services { get; }
    public IReadOnlyList<string> StartupTypes { get; } = Enum.GetNames<NssmStartupType>();
    public IReadOnlyList<string> ExitActions { get; } = Enum.GetNames<NssmExitAction>();
    public MptAsyncRelayCommand RefreshCommand { get; }
    public MptAsyncRelayCommand NewCommand { get; }
    public MptAsyncRelayCommand PreviewCommand { get; }
    public MptAsyncRelayCommand SaveCommand { get; }
    public MptAsyncRelayCommand DeleteCommand { get; }
    public MptAsyncRelayCommand StartCommand { get; }
    public MptAsyncRelayCommand StopCommand { get; }
    public MptAsyncRelayCommand RestartCommand { get; }
    public MptAsyncRelayCommand RotateCommand { get; }
    public MptAsyncRelayCommand MigrateCommand { get; }
    public MptAsyncRelayCommand RollbackCommand { get; }

    public NssmServiceSnapshot? SelectedService
    {
        get => _selectedService;
        set { if (SetProperty(ref _selectedService, value) && value is not null) _ = LoadAsync(value.Name); }
    }

    public bool Busy { get => _busy; private set { if (SetProperty(ref _busy, value)) { OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); } } }
    public bool CanEdit => !Busy && _loaded is not null;
    public bool IsExisting => _loaded is not null && !_isNew;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public string Application { get => _application; set => SetProperty(ref _application, value); }
    public string Parameters { get => _parameters; set => SetProperty(ref _parameters, value); }
    public string Directory { get => _directory; set => SetProperty(ref _directory, value); }
    public string Account { get => _account; set { if (SetProperty(ref _account, value)) { var localSystem = value.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase); var virtualAccount = value.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase); set_logon_enabled(localSystem, !localSystem && !virtualAccount); } } }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string StartupType { get => _startupType; set => SetProperty(ref _startupType, value); }
    public bool Interactive { get => _interactive; set => SetProperty(ref _interactive, value); }
    public string DependenciesText { get => _dependencies; set => SetProperty(ref _dependencies, value); }
    public string DependencyGroupsText { get => _dependencyGroups; set => SetProperty(ref _dependencyGroups, value); }
    public string ServiceEnvironmentText { get => _serviceEnvironment; set => SetProperty(ref _serviceEnvironment, value); }
    public string EnvironmentReplaceText { get => _environmentReplace; set => SetProperty(ref _environmentReplace, value); }
    public string EnvironmentText { get => _environment; set => SetProperty(ref _environment, value); }
    public string Priority { get => _priority; set => SetProperty(ref _priority, value); }
    public string Affinity { get => _affinity; set { if (SetProperty(ref _affinity, value)) AffinityEnabled = !value.Equals("All", StringComparison.OrdinalIgnoreCase); } }
    public string Stdin { get => _stdin; set => SetProperty(ref _stdin, value); }
    public string Stdout { get => _stdout; set => SetProperty(ref _stdout, value); }
    public string Stderr { get => _stderr; set => SetProperty(ref _stderr, value); }
    public uint StdinShare { get => _stdinShare; set => SetProperty(ref _stdinShare, value); }
    public uint StdoutShare { get => _stdoutShare; set => SetProperty(ref _stdoutShare, value); }
    public uint StderrShare { get => _stderrShare; set => SetProperty(ref _stderrShare, value); }
    public uint StdinDisposition { get => _stdinDisposition; set => SetProperty(ref _stdinDisposition, value); }
    public uint StdoutDisposition { get => _stdoutDisposition; set => SetProperty(ref _stdoutDisposition, value); }
    public uint StderrDisposition { get => _stderrDisposition; set => SetProperty(ref _stderrDisposition, value); }
    public uint StdinFlags { get => _stdinFlags; set => SetProperty(ref _stdinFlags, value); }
    public uint StdoutFlags { get => _stdoutFlags; set => SetProperty(ref _stdoutFlags, value); }
    public uint StderrFlags { get => _stderrFlags; set => SetProperty(ref _stderrFlags, value); }
    public bool StdoutCopyAndTruncate { get => _stdoutCopyAndTruncate; set => SetProperty(ref _stdoutCopyAndTruncate, value); }
    public bool StderrCopyAndTruncate { get => _stderrCopyAndTruncate; set => SetProperty(ref _stderrCopyAndTruncate, value); }
    public bool RotateFiles { get => _rotateFiles; set { if (SetProperty(ref _rotateFiles, value)) RotationOptionsEnabled = value; } }
    public bool RotateOnline { get => _rotateOnline; set => SetProperty(ref _rotateOnline, value); }
    public ulong RotateBytes { get => _rotateBytes; set => SetProperty(ref _rotateBytes, value); }
    public uint RotateSeconds { get => _rotateSeconds; set => SetProperty(ref _rotateSeconds, value); }
    public uint RotateDelay { get => _rotateDelay; set => SetProperty(ref _rotateDelay, value); }
    public bool TimestampLog { get => _timestampLog; set => SetProperty(ref _timestampLog, value); }
    public uint RestartDelay { get => _restartDelay; set => SetProperty(ref _restartDelay, value); }
    public uint Throttle { get => _throttle; set => SetProperty(ref _throttle, value); }
    public bool KillTree { get => _killTree; set => SetProperty(ref _killTree, value); }
    public uint StopMethodSkip { get => _stopMethodSkip; set { if (SetProperty(ref _stopMethodSkip, value)) { ConsoleTimeoutEnabled = (value & 1) == 0; WindowTimeoutEnabled = (value & 2) == 0; ThreadsTimeoutEnabled = (value & 4) == 0; } } }
    public uint StopConsole { get => _stopConsole; set => SetProperty(ref _stopConsole, value); }
    public uint StopWindow { get => _stopWindow; set => SetProperty(ref _stopWindow, value); }
    public uint StopThreads { get => _stopThreads; set => SetProperty(ref _stopThreads, value); }
    public bool NoConsole { get => _noConsole; set => SetProperty(ref _noConsole, value); }
    public string ExitAction { get => _exitAction; set => SetProperty(ref _exitAction, value); }
    public string ExitRulesText { get => _exitRules; set => SetProperty(ref _exitRules, value); }
    public string HooksText { get => _hooks; set { if (SetProperty(ref _hooks, value)) LoadSelectedHook(); } }
    public bool RedirectHookOutput { get => _redirectHookOutput; set => SetProperty(ref _redirectHookOutput, value); }
    public string Compatibility { get => _compatibility; private set => SetProperty(ref _compatibility, value); }
    public string Impact { get => _impact; private set => SetProperty(ref _impact, value); }
    public bool ImpactConfirmed { get => _impactConfirmed; set => SetProperty(ref _impactConfirmed, value); }
}
