using System.Security.Cryptography;
using Google.Protobuf;
using MyPowerTools.HostControl;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

internal sealed record ShellHomeSnapshot(
    IReadOnlyList<HostProto.ToolDescriptor> Tools,
    string Fingerprint,
    byte[] Payload);

internal static class ShellHomeSnapshotCache
{
    private const int MaximumPayloadBytes = 4 * 1024 * 1024;
    private const string SnapshotFileName = "shell-home-tools.v1.pb";

    public static async Task<ShellHomeSnapshot?> TryReadAsync(
        string? dataRoot,
        CancellationToken cancellationToken = default)
    {
        var path = SnapshotPath(dataRoot);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var file = new FileInfo(path);
            if (file.Length > MaximumPayloadBytes)
            {
                return null;
            }

            var payload = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var response = HostProto.ListToolsResponse.Parser.ParseFrom(payload);
            ShellStartupDiagnostics.Mark("home-cache-ready");
            return new ShellHomeSnapshot(
                response.Tools.ToArray(),
                Fingerprint(payload),
                payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidProtocolBufferException)
        {
            return null;
        }
    }

    public static ShellHomeSnapshot Create(IEnumerable<HostProto.ToolDescriptor> tools)
    {
        var response = new HostProto.ListToolsResponse();
        response.Tools.AddRange(tools);
        var payload = response.ToByteArray();
        return new ShellHomeSnapshot(
            response.Tools.ToArray(),
            Fingerprint(payload),
            payload);
    }

    public static async Task WriteAsync(
        ShellHomeSnapshot snapshot,
        string? dataRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidOperationException("The Home snapshot payload is outside the supported size range.");
        }

        var path = SnapshotPath(dataRoot);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The Home snapshot path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, snapshot.Payload, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static string SnapshotPath(string? dataRoot)
    {
        return Path.Combine(
            dataRoot ?? HostControlAuthTokenStore.DefaultDataRoot(),
            "state",
            SnapshotFileName);
    }

    private static string Fingerprint(byte[] payload)
    {
        return Convert.ToHexString(SHA256.HashData(payload));
    }
}
