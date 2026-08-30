using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;

namespace NssmManager.Runtime;

public sealed class NssmElevatedOperationException(
    string operation,
    string message,
    string remoteExceptionType,
    int? nativeErrorCode) : Exception(message)
{
    public string Operation { get; } = operation;
    public string RemoteExceptionType { get; } = remoteExceptionType;
    public int? NativeErrorCode { get; } = nativeErrorCode;
}

public static class NssmElevatedClient
{
    private const int MaximumResultBytes = 1024 * 1024;

    public static Task<JsonNode> ExecuteAsync(string operation, JsonObject arguments, CancellationToken cancellationToken = default) =>
        ExecuteAsync(operation, arguments, null, cancellationToken);

    public static async Task<JsonNode> ExecuteAsync(string operation, JsonObject arguments, char[]? password, CancellationToken cancellationToken = default)
    {
        var safeArguments = arguments.DeepClone().AsObject();
        safeArguments.Remove("password");
        var passwordLength = password is null ? 0 : password.Length > 0 && password[^1] == '\0' ? password.Length - 1 : password.Length;
        var pipeName = passwordLength == 0 ? null : "mpt-nssm-secret-" + Guid.NewGuid().ToString("N");
        using var secretPipe = pipeName is null ? null : CreateSecretPipe(pipeName);
        if (pipeName is not null) safeArguments["passwordPipe"] = pipeName;
        var brokerPath = ResolveBrokerPath();
        using var brokerLock = new FileStream(brokerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var brokerHash = Convert.ToHexString(await SHA256.HashDataAsync(brokerLock, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        var requestRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "broker-requests", "nssm-manager");
        Directory.CreateDirectory(requestRoot);
        EnsureNoReparsePoint(requestRoot);
        var token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var requestPath = Path.Combine(requestRoot, token + ".json");
        var resultPath = Path.Combine(requestRoot, token + ".result.json");
        var now = DateTimeOffset.UtcNow;
        var request = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["token"] = token,
            ["moduleId"] = "nssm-manager",
            ["operation"] = operation,
            ["createdAt"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["expiresAt"] = now.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture),
            ["arguments"] = safeArguments,
            ["broker"] = new JsonObject { ["path"] = brokerPath, ["sha256"] = brokerHash }
        };
        var bytes = Encoding.UTF8.GetBytes(request.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        WriteNewFile(requestPath, bytes);
        try
        {
            var alreadyElevated = IsCurrentProcessElevated();
            var info = new ProcessStartInfo
            {
                FileName = brokerPath,
                WorkingDirectory = Path.GetDirectoryName(brokerPath)!,
                UseShellExecute = !alreadyElevated,
                CreateNoWindow = alreadyElevated,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (!alreadyElevated) info.Verb = "runas";
            foreach (var argument in new[] { "nssm-service", "execute-request", "--request-file", requestPath, "--token", token, "--digest", digest, "--broker-sha256", brokerHash }) info.ArgumentList.Add(argument);
            using var process = StartElevated(info);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            var processExit = process.WaitForExitAsync(deadline.Token);
            if (secretPipe is not null)
            {
                var connection = secretPipe.WaitForConnectionAsync(deadline.Token);
                if (await Task.WhenAny(connection, processExit).ConfigureAwait(false) == processExit)
                {
                    await processExit.ConfigureAwait(false);
                    throw new InvalidOperationException($"Elevated Broker exited before accepting the protected password (exit code {process.ExitCode}).");
                }
                await connection.ConfigureAwait(false);
                var secretBytes = new byte[Encoding.UTF8.GetMaxByteCount(passwordLength)];
                var secretLength = Encoding.UTF8.GetBytes(password!.AsSpan(0, passwordLength), secretBytes.AsSpan());
                var protectedBytes = new byte[Math.Max(16, ((secretLength + 15) / 16) * 16)];
                try
                {
                    secretBytes.AsSpan(0, secretLength).CopyTo(protectedBytes);
                    if (!CryptProtectMemory(protectedBytes, (uint)protectedBytes.Length, 2)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptProtectMemory");
                    await secretPipe.WriteAsync(BitConverter.GetBytes(secretLength), deadline.Token).ConfigureAwait(false);
                    await secretPipe.WriteAsync(BitConverter.GetBytes(protectedBytes.Length), deadline.Token).ConfigureAwait(false);
                    await secretPipe.WriteAsync(protectedBytes, deadline.Token).ConfigureAwait(false);
                    await secretPipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(secretBytes); CryptographicOperations.ZeroMemory(protectedBytes); }
            }
            await processExit.ConfigureAwait(false);
            if (File.Exists(resultPath)) return await ReadResultAsync(resultPath, token, digest, operation, cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"Elevated Broker rejected NSSM operation (exit code {process.ExitCode}).");
            throw new InvalidDataException("Elevated Broker produced no trusted result.");
        }
        finally { TryDelete(requestPath); TryDelete(resultPath); }
    }

    private static async Task<JsonNode> ReadResultAsync(string path, string token, string digest, string operation, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Elevated Broker produced no trusted result.");
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumResultBytes) throw new InvalidDataException("Elevated Broker result size is invalid.");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))?.AsObject() ?? throw new InvalidDataException("Elevated Broker result is invalid.");
        if (root["schemaVersion"]?.GetValue<int>() != 1 || root["token"]?.GetValue<string>() != token || !FixedEquals(root["requestDigest"]?.GetValue<string>() ?? "", digest)) throw new InvalidDataException("Elevated Broker result does not match this request.");
        if (root["success"]?.GetValue<bool>() != true)
        {
            var error = root["payload"] as JsonObject;
            var remoteType = error?["exceptionType"]?.GetValue<string>() ?? "Exception";
            int? nativeError = error?["nativeErrorCode"] is JsonValue nativeValue && nativeValue.TryGetValue<int>(out var code) ? code : null;
            throw new NssmElevatedOperationException(operation, root["message"]?.GetValue<string>() ?? "Elevated Broker failed.", remoteType, nativeError);
        }
        return root["payload"] ?? new JsonObject();
    }

    private static string ResolveBrokerPath()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MyPowerTools", "Broker", "MyPowerTools.ElevatedBroker.exe");
        string? path = null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); path is null && directory is not null; directory = directory.Parent)
        {
            var candidates = new[]
            {
                Path.Combine(directory.FullName, "artifacts", "build", "bin", "MyPowerTools.ElevatedBroker", "release", "MyPowerTools.ElevatedBroker.exe"),
                Path.Combine(directory.FullName, "src", "MyPowerTools.ElevatedBroker", "bin", "Release", "net10.0", "win-x64", "MyPowerTools.ElevatedBroker.exe")
            };
            path = candidates.FirstOrDefault(File.Exists);
        }
        if (path is null && File.Exists(installed)) path = installed;
        if (path is null) throw new FileNotFoundException("MyPowerTools Elevated Broker is not installed or built.");
        EnsureNoReparsePoint(path);
        return Path.GetFullPath(path);
    }

    public static string ResolveManagedExecutable()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidates = new[]
            {
                Path.Combine(directory.FullName, "src", "NssmManager.Executable", "publish", "win-x64", "nssm-manager.exe"),
                Path.Combine(directory.FullName, "nssm-manager.exe")
            };
            var found = candidates.FirstOrDefault(File.Exists);
            if (found is not null) return Path.GetFullPath(found);
        }
        throw new FileNotFoundException("Packaged nssm-manager.exe was not found.");
    }

    private static Process StartElevated(ProcessStartInfo info)
    {
        try { return Process.Start(info) ?? throw new InvalidOperationException("Elevated Broker failed to start."); }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223) { throw new OperationCanceledException("Windows UAC confirmation was cancelled.", exception); }
    }
    private static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    private static NamedPipeServerStream CreateSecretPipe(string pipeName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected password transport requires Windows.");
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var user = identity.User ?? throw new UnauthorizedAccessException("The current Windows user SID is unavailable.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
        {
            user,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        })
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security, HandleInheritability.None);
    }
    private static void WriteNewFile(string path, byte[] bytes) { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough); stream.Write(bytes); stream.Flush(true); }
    private static void EnsureNoReparsePoint(string path) { var item = File.Exists(path) ? new FileInfo(path) as FileSystemInfo : new DirectoryInfo(path); while (item is not null) { if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Broker path contains a reparse point."); item = item switch { FileInfo file => file.Directory, DirectoryInfo directory => directory.Parent, _ => null }; } }
    private static bool FixedEquals(string left, string right) => left.Length == right.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left.ToLowerInvariant()), Encoding.ASCII.GetBytes(right.ToLowerInvariant()));
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectMemory([In, Out] byte[] data, uint dataLength, uint flags);
}
