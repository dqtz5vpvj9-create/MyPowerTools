using System.Globalization;

namespace MyPowerTools.Shell.Avalonia.Services;

internal enum ShellFailureKind
{
    ServiceUnavailable,
    ServiceTimeout,
    CredentialMismatch,
    AccessDenied,
    PermissionDenied,
    FileMissing,
    DataInvalid,
    Unexpected,
    SurfaceUnavailable
}

internal enum ShellRecoveryStep
{
    Reload,
    StartService,
    RestartService,
    Repair,
    ReviewPermissions,
    RestartApp
}

internal sealed record ShellRecoveryPresentation(string ActionLabel, string ActionHint, string AttemptText);

/// <summary>
/// The recovery ladder (docs/desktop/ui-recovery-contract.md). Every rung is stronger than the one
/// before it, so pressing the recovery button never repeats the same no-op.
/// </summary>
internal static class ShellRecoveryPlan
{
    internal static bool CanRestartApp =>
        !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS() && !OperatingSystem.IsBrowser();

    public static ShellFailureKind KindOf(string code)
    {
        if (code.EndsWith("-unavailable", StringComparison.Ordinal)) return ShellFailureKind.ServiceUnavailable;
        if (code.EndsWith("-timeout", StringComparison.Ordinal)) return ShellFailureKind.ServiceTimeout;
        if (code.EndsWith("-credential-mismatch", StringComparison.Ordinal)) return ShellFailureKind.CredentialMismatch;
        if (code.EndsWith("-access-denied", StringComparison.Ordinal)) return ShellFailureKind.AccessDenied;
        if (code.EndsWith("-permission-denied", StringComparison.Ordinal)) return ShellFailureKind.PermissionDenied;
        return code switch
        {
            "local-file-missing" => ShellFailureKind.FileMissing,
            "local-data-invalid" => ShellFailureKind.DataInvalid,
            "surface-unavailable" => ShellFailureKind.SurfaceUnavailable,
            _ => ShellFailureKind.Unexpected
        };
    }

    public static IReadOnlyList<ShellRecoveryStep> Ladder(ShellFailureKind kind)
    {
        ShellRecoveryStep[] ladder = kind switch
        {
            ShellFailureKind.ServiceUnavailable or ShellFailureKind.ServiceTimeout =>
                [ShellRecoveryStep.StartService, ShellRecoveryStep.RestartService, ShellRecoveryStep.RestartApp],
            ShellFailureKind.CredentialMismatch or ShellFailureKind.AccessDenied =>
                [ShellRecoveryStep.RestartService, ShellRecoveryStep.RestartApp],
            ShellFailureKind.PermissionDenied =>
                [ShellRecoveryStep.Reload, ShellRecoveryStep.ReviewPermissions, ShellRecoveryStep.RestartApp],
            ShellFailureKind.FileMissing or ShellFailureKind.DataInvalid =>
                [ShellRecoveryStep.Reload, ShellRecoveryStep.Repair, ShellRecoveryStep.RestartApp],
            ShellFailureKind.SurfaceUnavailable =>
                [ShellRecoveryStep.Reload, ShellRecoveryStep.RestartService, ShellRecoveryStep.RestartApp],
            _ => [ShellRecoveryStep.Reload, ShellRecoveryStep.RestartApp]
        };

        return CanRestartApp
            ? ladder
            : ladder.Where(step => step != ShellRecoveryStep.RestartApp).ToArray();
    }

    public static ShellRecoveryStep StepFor(ShellFailureKind kind, int attempt)
    {
        var ladder = Ladder(kind);
        return ladder[Math.Clamp(attempt - 1, 0, ladder.Count - 1)];
    }

    public static ShellRecoveryPresentation Present(
        ShellRecoveryStep step,
        ShellFailureSource source,
        ShellRecoveryTracker.Entry entry)
    {
        var attemptText = entry.LastStep is { } last
            ? string.Format(
                CultureInfo.InvariantCulture,
                "Tried at {0:HH:mm:ss}: {1}. Still not working{2}",
                entry.LastAt.ToLocalTime(),
                Describe(last, source),
                last == step ? "." : $", so the next step is stronger.")
            : "";
        return new ShellRecoveryPresentation(ActionLabel(step, source), ActionHint(step, source), attemptText);
    }

    public static string ActionLabel(ShellRecoveryStep step, ShellFailureSource source) => step switch
    {
        ShellRecoveryStep.RestartService => source == ShellFailureSource.ServiceManager
            ? "Restart service manager"
            : "Restart background service",
        ShellRecoveryStep.Repair => "Repair MyPowerTools",
        ShellRecoveryStep.ReviewPermissions => "Review permissions",
        ShellRecoveryStep.RestartApp => "Restart MyPowerTools",
        _ => "Try again"
    };

    public static string ActionHint(ShellRecoveryStep step, ShellFailureSource source) => step switch
    {
        ShellRecoveryStep.StartService => $"Starts the {ServiceName(source)} if it isn't running, then reloads.",
        ShellRecoveryStep.RestartService => $"Restarts the {ServiceName(source)}, waits until it responds, then reloads.",
        ShellRecoveryStep.Repair => "Opens Updates, where you can repair or reinstall what's missing.",
        ShellRecoveryStep.ReviewPermissions => "Opens Components, where you can turn on what this page needs.",
        ShellRecoveryStep.RestartApp => "Closes and reopens MyPowerTools. Tools running in the background keep running.",
        _ => "Reloads this page."
    };

    public static string Describe(ShellRecoveryStep step, ShellFailureSource source) => step switch
    {
        ShellRecoveryStep.StartService => $"checked the {ServiceName(source)} and reloaded",
        ShellRecoveryStep.RestartService => $"restarted the {ServiceName(source)} and reloaded",
        ShellRecoveryStep.Repair => "opened repair",
        ShellRecoveryStep.ReviewPermissions => "opened permissions",
        ShellRecoveryStep.RestartApp => "restarted MyPowerTools",
        _ => "reloaded"
    };

    public static string ServiceName(ShellFailureSource source) =>
        source == ShellFailureSource.ServiceManager ? "service manager" : "background service";
}

/// <summary>
/// Remembers recovery attempts per surface so a failure that survives a rung advances to the next
/// one, while unrelated background refresh failures do not burn rungs on the user's behalf.
/// </summary>
internal sealed class ShellRecoveryTracker
{
    private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;

    public ShellRecoveryTracker(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public sealed class Entry
    {
        public int Attempt { get; internal set; } = 1;
        public bool Running { get; internal set; }
        public ShellRecoveryStep? LastStep { get; internal set; }
        public DateTimeOffset LastAt { get; internal set; }
        public DateTimeOffset Touched { get; internal set; }
    }

    public Entry RecordFailure(string key)
    {
        var now = _clock();
        Prune(now);
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry();
            _entries[key] = entry;
        }
        else if (entry.Running)
        {
            entry.Attempt++;
            entry.Running = false;
        }

        entry.Touched = now;
        return entry;
    }

    public Entry? Find(string key)
    {
        Prune(_clock());
        return _entries.TryGetValue(key, out var entry) ? entry : null;
    }

    public void BeginAttempt(string key, ShellRecoveryStep step)
    {
        var entry = RecordFailure(key);
        entry.Running = true;
        entry.LastStep = step;
        entry.LastAt = _clock();
    }

    public void Clear(string key) => _entries.Remove(key);

    private void Prune(DateTimeOffset now)
    {
        foreach (var stale in _entries.Where(item => now - item.Value.Touched > Expiry).Select(item => item.Key).ToArray())
        {
            _entries.Remove(stale);
        }
    }
}
