using System.Diagnostics;
using System.Text;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Linux;

/// <summary>Persists secrets in the user's Secret Service collection through libsecret.</summary>
public sealed class LinuxSecretStore : ISecretStore
{
    private readonly string? _helperPath;

    public LinuxSecretStore() : this(FindHelper()) { }

    // An explicit executable also lets hosts supply a bundled libsecret helper.
    public LinuxSecretStore(string? helperPath) => _helperPath = helperPath;

    public bool IsAvailable => OperatingSystem.IsLinux() && _helperPath is not null && IsExecutable(_helperPath);

    public async Task<SecretReference> SaveAsync(string moduleId, string name, string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(secret);
        var reference = SecretReference.Create(moduleId, name);
        var result = await RunAsync(["store", "--label=MyPowerTools", "application", "com.mypowertools.secrets", "module", moduleId, "name", name], secret, cancellationToken);
        ThrowIfFailed(result, "save");
        return reference;
    }

    public async Task<string?> ReadAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (moduleId, name) = Parts(reference);
        var result = await RunAsync(["lookup", "application", "com.mypowertools.secrets", "module", moduleId, "name", name], null, cancellationToken);
        if (IsMissing(result)) return null;
        ThrowIfFailed(result, "read");
        // secret-tool appends one newline; preserve whitespace belonging to the secret.
        return result.Output.EndsWith('\n') ? result.Output[..^1] : result.Output;
    }

    public async Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (moduleId, name) = Parts(reference);
        var result = await RunAsync(["clear", "application", "com.mypowertools.secrets", "module", moduleId, "name", name], null, cancellationToken);
        if (!IsMissing(result)) ThrowIfFailed(result, "delete");
    }

    private static (string ModuleId, string Name) Parts(SecretReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!reference.TryGetParts(out var moduleId, out var name))
            throw new ArgumentException("Secret reference is invalid.", nameof(reference));
        return (moduleId, name);
    }

    private static bool IsMissing(Result result) => result.ExitCode == 1 && result.Error.Length == 0 && result.Output.Length == 0;

    private static void ThrowIfFailed(Result result, string operation)
    {
        if (result.ExitCode != 0)
            // Helper diagnostics may contain sensitive data, so never include them in errors.
            throw new InvalidOperationException($"Linux Secret Service {operation} failed with exit status {result.ExitCode}. Ensure the session D-Bus and an unlocked Secret Service keyring are available.");
    }

    private async Task<Result> RunAsync(string[] arguments, string? input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
            throw new PlatformNotSupportedException("Linux Secret Service requires the executable secret-tool helper from libsecret-tools.");
        var startInfo = new ProcessStartInfo(_helperPath!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            cancellationToken.ThrowIfCancellationRequested();
            return new Result(process.ExitCode, output, error);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static string? FindHelper()
    {
        if (!OperatingSystem.IsLinux()) return null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.GetFullPath(Path.Combine(directory, "secret-tool"));
            if (IsExecutable(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsExecutable(string path) => File.Exists(path) && OperatingSystem.IsLinux() &&
        (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    private sealed record Result(int ExitCode, string Output, string Error);
}
