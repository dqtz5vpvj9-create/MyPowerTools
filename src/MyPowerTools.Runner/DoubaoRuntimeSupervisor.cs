using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace MyPowerTools.Runner;

public sealed record DoubaoSupervisorCycleResult(
    string State,
    bool AttemptedStart,
    bool AllServicesOnline,
    bool ManifestStale,
    int ConsecutiveFailures);

public sealed class DoubaoRuntimeSupervisor : IAsyncDisposable
{
    private static readonly int[] TargetPorts = [38102, 38080, 38189];

    public static IReadOnlyList<int> ServicePorts => TargetPorts;

    private const int MaximumConsecutiveFailures = 3;
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(75);
    private readonly string _shellPath;
    private readonly string _runtimeRoot;
    private readonly string _doubaoDataRoot;
    private readonly string _settingsPath;
    private readonly string _manifestPath;
    private readonly string _logPath;
    private readonly Func<int, CancellationToken, Task<bool>> _portProbe;
    private readonly Func<CancellationToken, Task<int>> _runtimeStarter;
    private readonly Func<int, bool> _processExists;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private Task? _loopTask;
    private DateTimeOffset _retryNotBeforeUtc = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private string? _lastLoggedState;
    private bool _disposed;

    public DoubaoRuntimeSupervisor(
        string appRoot,
        string dataRoot,
        Func<int, CancellationToken, Task<bool>>? portProbe = null,
        Func<CancellationToken, Task<int>>? runtimeStarter = null,
        Func<int, bool>? processExists = null,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var fullAppRoot = Path.GetFullPath(appRoot);
        var fullDataRoot = Path.GetFullPath(dataRoot);
        _shellPath = Path.Combine(fullAppRoot, "Shell", "MyPowerTools.Shell.Avalonia.exe");
        _runtimeRoot = Path.Combine(fullAppRoot, "Runtimes", "Doubao");
        _doubaoDataRoot = Path.Combine(fullDataRoot, "Doubao");
        _settingsPath = Path.Combine(_doubaoDataRoot, "settings.json");
        _manifestPath = Path.Combine(_doubaoDataRoot, "logs", "mypowertools-secure-runtime.json");
        _logPath = Path.Combine(fullDataRoot, "logs", "doubao-runtime-supervisor.jsonl");
        _portProbe = portProbe ?? ProbeLoopbackPortAsync;
        _processExists = processExists ?? ProcessExists;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _runtimeStarter = runtimeStarter ?? StartRuntimeAsync;
    }

    public bool HasInstalledLayout => File.Exists(_shellPath) && Directory.Exists(_runtimeRoot);

    public static DoubaoRuntimeSupervisor? CreateForInstalledLayout(string appRoot, string dataRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var supervisor = new DoubaoRuntimeSupervisor(appRoot, dataRoot);
        if (supervisor.HasInstalledLayout)
        {
            return supervisor;
        }

        supervisor.DisposeWithoutLoop();
        return null;
    }

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lifecycleGate)
        {
            _loopTask ??= Task.Run(() => RunLoopAsync(_shutdown.Token));
        }

        return Task.CompletedTask;
    }

    public async Task<DoubaoSupervisorCycleResult> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool autoStartEnabled;
            try
            {
                autoStartEnabled = ReadAutoStartEnabled(_settingsPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                await LogStateAsync(
                    "settings-invalid",
                    "warning",
                    "豆包自动启动设置暂时无法读取；本轮监督已跳过。",
                    force: false,
                    errorType: ex.GetType().Name).ConfigureAwait(false);
                return Result("settings-invalid", attemptedStart: false, allOnline: false, manifestStale: false);
            }

            if (!autoStartEnabled)
            {
                ResetFailures();
                await LogStateAsync(
                    "disabled",
                    "info",
                    "豆包运行时自动启动已关闭。",
                    force: false).ConfigureAwait(false);
                return Result("disabled", attemptedStart: false, allOnline: false, manifestStale: false);
            }

            if (!HasInstalledLayout)
            {
                await LogStateAsync(
                    "layout-missing",
                    "warning",
                    "豆包安装运行时或 Shell 命令入口缺失。",
                    force: false).ConfigureAwait(false);
                return Result("layout-missing", attemptedStart: false, allOnline: false, manifestStale: false);
            }

            var before = await ProbeAllPortsAsync(cancellationToken).ConfigureAwait(false);
            if (before.All(value => value))
            {
                ResetFailures();
                await LogStateAsync(
                    "online",
                    "info",
                    "豆包三个 loopback 服务均在线。",
                    force: false).ConfigureAwait(false);
                return Result("online", attemptedStart: false, allOnline: true, manifestStale: false);
            }

            var manifestStale = IsManifestStale(_manifestPath, _processExists);
            if (before.Any(value => value))
            {
                await LogStateAsync(
                    "partial-online",
                    "warning",
                    "豆包服务处于部分在线状态；为避免重复占用固定端口，本轮未启动新进程。",
                    force: false).ConfigureAwait(false);
                return Result("partial-online", attemptedStart: false, allOnline: false, manifestStale);
            }

            var now = _timeProvider.GetUtcNow();
            if (now < _retryNotBeforeUtc)
            {
                return Result("backoff", attemptedStart: false, allOnline: false, manifestStale);
            }

            if (_consecutiveFailures >= MaximumConsecutiveFailures)
            {
                _consecutiveFailures = 0;
            }

            var attempt = _consecutiveFailures + 1;
            await LogStateAsync(
                "start-attempt",
                "info",
                manifestStale
                    ? "三个服务均离线且所有权清单已陈旧；正在通过 Shell 安全入口恢复。"
                    : "三个服务均离线；正在通过 Shell 安全入口启动。",
                force: true,
                attempt: attempt).ConfigureAwait(false);

            int exitCode;
            try
            {
                exitCode = await _runtimeStarter(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                exitCode = -1;
                await WriteLogAsync(new SupervisorLogEntry(
                    _timeProvider.GetUtcNow(),
                    "error",
                    "start-exception",
                    "豆包 Shell 启动入口执行失败。",
                    attempt,
                    ex.GetType().Name)).ConfigureAwait(false);
            }

            var after = await ProbeAllPortsAsync(cancellationToken).ConfigureAwait(false);
            if (after.All(value => value))
            {
                ResetFailures();
                await LogStateAsync(
                    "recovered",
                    "info",
                    "豆包三个服务已恢复在线。",
                    force: true,
                    attempt: attempt).ConfigureAwait(false);
                return Result("recovered", attemptedStart: true, allOnline: true, manifestStale);
            }

            RegisterFailure(now);
            await LogStateAsync(
                "start-failed",
                "warning",
                $"豆包启动入口退出码 {exitCode}；服务仍未全部在线。",
                force: true,
                attempt: attempt).ConfigureAwait(false);
            return Result("start-failed", attemptedStart: true, allOnline: false, manifestStale);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    public static bool ReadAutoStartEnabled(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return true;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Doubao settings root must be an object.");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, "AutoStartEnabled", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(property.Value.GetString(), out var parsed) => parsed,
                _ => throw new InvalidDataException("AutoStartEnabled must be a Boolean value.")
            };
        }

        return true;
    }

    public static bool IsManifestStale(string manifestPath, Func<int, bool>? processExists = null)
    {
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        processExists ??= ProcessExists;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement processes = default;
            var found = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "Processes", StringComparison.OrdinalIgnoreCase))
                {
                    processes = property.Value;
                    found = true;
                    break;
                }
            }

            if (!found || processes.ValueKind != JsonValueKind.Array)
            {
                return true;
            }

            var processIds = new List<int>();
            foreach (var process in processes.EnumerateArray())
            {
                if (process.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var property in process.EnumerateObject())
                {
                    if (string.Equals(property.Name, "ProcessId", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.TryGetInt32(out var processId) &&
                        processId > 0)
                    {
                        processIds.Add(processId);
                        break;
                    }
                }
            }

            return processIds.Count == 0 || processIds.All(processId => !processExists(processId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loopTask;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
            loopTask = _loopTask;
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cycleGate.Dispose();
        _shutdown.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await WriteLogAsync(new SupervisorLogEntry(
                    _timeProvider.GetUtcNow(),
                    "error",
                    "cycle-exception",
                    "豆包监督周期发生未处理错误。",
                    0,
                    ex.GetType().Name)).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<bool[]> ProbeAllPortsAsync(CancellationToken cancellationToken)
    {
        var probes = TargetPorts.Select(port => _portProbe(port, cancellationToken)).ToArray();
        return await Task.WhenAll(probes).ConfigureAwait(false);
    }

    private async Task<int> StartRuntimeAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _shellPath,
            WorkingDirectory = Path.GetDirectoryName(_shellPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--doubao-runtime");
        startInfo.ArgumentList.Add("start");
        startInfo.ArgumentList.Add("--doubao-runtime-root");
        startInfo.ArgumentList.Add(_runtimeRoot);
        startInfo.ArgumentList.Add("--doubao-data-root");
        startInfo.ArgumentList.Add(_doubaoDataRoot);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Doubao Shell command process did not start.");
        using var timeout = new CancellationTokenSource(StartTimeout);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(waitCancellation.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return -2;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or Win32Exception)
            {
            }

            return -3;
        }
    }

    private static async Task<bool> ProbeLoopbackPortAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static bool ProcessExists(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private void RegisterFailure(DateTimeOffset now)
    {
        _consecutiveFailures++;
        _retryNotBeforeUtc = _consecutiveFailures >= MaximumConsecutiveFailures
            ? now.Add(MaximumBackoff)
            : now.Add(TimeSpan.FromSeconds(15 * Math.Pow(2, _consecutiveFailures - 1)));
    }

    private void ResetFailures()
    {
        _consecutiveFailures = 0;
        _retryNotBeforeUtc = DateTimeOffset.MinValue;
    }

    private DoubaoSupervisorCycleResult Result(string state, bool attemptedStart, bool allOnline, bool manifestStale) =>
        new(state, attemptedStart, allOnline, manifestStale, _consecutiveFailures);

    private async Task LogStateAsync(
        string state,
        string level,
        string message,
        bool force,
        int attempt = 0,
        string? errorType = null)
    {
        if (!force && string.Equals(_lastLoggedState, state, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedState = state;
        await WriteLogAsync(new SupervisorLogEntry(
            _timeProvider.GetUtcNow(),
            level,
            state,
            message,
            attempt,
            errorType)).ConfigureAwait(false);
    }

    private async Task WriteLogAsync(SupervisorLogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            var line = JsonSerializer.Serialize(entry);
            await File.AppendAllTextAsync(_logPath, line + Environment.NewLine).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void DisposeWithoutLoop()
    {
        _disposed = true;
        _cycleGate.Dispose();
        _shutdown.Dispose();
    }

    private sealed record SupervisorLogEntry(
        DateTimeOffset TimeUtc,
        string Level,
        string Event,
        string Message,
        int Attempt,
        string? ErrorType);
}
