using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalLagCleaner.MyPowerTools;

namespace LocalLagCleaner.Runtime;

internal sealed record ElevatedFileHandleDiagnosticResult(
    SystemFileHandleAttribution Attribution,
    IReadOnlyList<FileHandlePathGroupSnapshot> PathGroups,
    IReadOnlyList<FileSystemFilterInstanceSnapshot> FilterInstances,
    bool DebugPrivilegeEnabled);

internal static class ElevatedFileHandleDiagnosticClient
{
    private const string ModuleId = "local-lag-cleaner";
    private const string ActionId = "system-file-handle-path-sample";
    private const int MaximumResultBytes = 4 * 1024 * 1024;

    public static async Task<ElevatedFileHandleDiagnosticResult> RunAsync(
        ushort fileTypeIndex,
        ulong expectedFileHandles,
        int maximumSamples,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "管理员 File 句柄归因仅支持 Windows。");
        }
        if (fileTypeIndex == 0 ||
            expectedFileHandles == 0 ||
            maximumSamples is < 1 or > 512)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSamples),
                "管理员 File 句柄归因参数超出安全边界。");
        }

        var brokerPath = ResolveBrokerPath();
        using var brokerLock = new FileStream(
            brokerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var brokerHash = Convert.ToHexString(
                await SHA256.HashDataAsync(
                    brokerLock,
                    cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        brokerLock.Position = 0;

        var requestDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools",
            "broker-requests",
            ModuleId));
        Directory.CreateDirectory(requestDirectory);
        EnsureNoReparsePoint(requestDirectory);
        var token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var requestPath = Path.Combine(requestDirectory, $"{token}.json");
        var resultPath = Path.Combine(requestDirectory, $"{token}.result.json");
        var now = DateTimeOffset.UtcNow;
        var request = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["token"] = token,
            ["moduleId"] = ModuleId,
            ["action"] = ActionId,
            ["createdAt"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["expiresAt"] = now.AddMinutes(5).ToString(
                "O",
                CultureInfo.InvariantCulture),
            ["fileTypeIndex"] = fileTypeIndex,
            ["expectedFileHandles"] = expectedFileHandles,
            ["maximumSamples"] = maximumSamples,
            ["broker"] = new JsonObject
            {
                ["path"] = brokerPath,
                ["sha256"] = brokerHash
            }
        };
        var requestBytes = Encoding.UTF8.GetBytes(
            request.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }));
        var requestDigest = Convert.ToHexString(SHA256.HashData(requestBytes))
            .ToLowerInvariant();
        WriteNewFile(requestPath, requestBytes);

        try
        {
            var launch = new ProcessStartInfo
            {
                FileName = brokerPath,
                WorkingDirectory = Path.GetDirectoryName(brokerPath)!,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in new[]
                     {
                         "diagnostics",
                         "file-handles",
                         "--request-file",
                         requestPath,
                         "--token",
                         token,
                         "--digest",
                         requestDigest,
                         "--broker-sha256",
                         brokerHash
                     })
            {
                launch.ArgumentList.Add(argument);
            }

            using var process = StartElevated(launch);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException(
                    "管理员 File 句柄归因在三分钟内没有完成。");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"管理员 Broker 拒绝或未完成 File 句柄归因（退出码 {process.ExitCode}）。");
            }
            return await ReadResultAsync(
                resultPath,
                token,
                requestDigest,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    private static string ResolveBrokerPath()
    {
        var path = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "MyPowerTools",
            "Broker",
            "MyPowerTools.ElevatedBroker.exe"));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "MyPowerTools 管理员 Broker 尚未安装，请更新本机 MPT Core。",
                path);
        }
        EnsureNoReparsePoint(path);
        return path;
    }

    private static Process StartElevated(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ??
                   throw new InvalidOperationException(
                       "管理员 Broker 进程未能启动。");
        }
        catch (Win32Exception exception)
            when (exception.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException(
                "Windows UAC 管理员确认已取消。",
                exception);
        }
    }

    private static async Task<ElevatedFileHandleDiagnosticResult> ReadResultAsync(
        string path,
        string token,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "管理员 Broker 没有生成可信的 File 句柄归因结果。");
        }

        var information = new FileInfo(path);
        if (information.Length is <= 0 or > MaximumResultBytes)
        {
            throw new InvalidDataException(
                "管理员 File 句柄归因结果超出安全边界。");
        }

        var json = await File.ReadAllTextAsync(
            path,
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        var root = JsonNode.Parse(
                       json,
                       documentOptions: new JsonDocumentOptions
                       {
                           AllowTrailingCommas = false,
                           CommentHandling = JsonCommentHandling.Disallow,
                           MaxDepth = 12
                       })?.AsObject() ??
                   throw new InvalidDataException(
                       "管理员 File 句柄归因结果格式错误。");
        if (root["schemaVersion"]?.GetValue<int>() != 1 ||
            !string.Equals(
                root["token"]?.GetValue<string>(),
                token,
                StringComparison.Ordinal) ||
            !FixedHexEquals(
                root["requestDigest"]?.GetValue<string>() ?? "",
                requestDigest))
        {
            throw new InvalidDataException(
                "管理员 File 句柄归因结果与当前请求不匹配。");
        }

        var total = root["totalFileHandles"]?.GetValue<ulong>() ?? 0;
        var requested = root["requestedSamples"]?.GetValue<int>() ?? 0;
        var attempted = root["attemptedSamples"]?.GetValue<int>() ?? 0;
        var duplicated = root["duplicatedSamples"]?.GetValue<int>() ?? 0;
        var resolved = root["resolvedPathSamples"]?.GetValue<int>() ?? 0;
        var summary = root["summary"]?.GetValue<string>() ?? "";
        var requiresKernelDriver =
            root["requiresKernelDriver"]?.GetValue<bool>() == true;
        var nativeErrorCode = root["nativeErrorCode"]?.GetValue<int>() ?? 0;
        if (total == 0 ||
            requested is < 1 or > 512 ||
            (!requiresKernelDriver && attempted != requested) ||
            (requiresKernelDriver && attempted != 0) ||
            duplicated is < 0 ||
            duplicated > attempted ||
            resolved is < 0 ||
            resolved > duplicated ||
            summary.Length is 0 or > 4096)
        {
            throw new InvalidDataException(
                "管理员 File 句柄归因结果计数无效。");
        }

        var groups = new List<FileHandlePathGroupSnapshot>();
        foreach (var item in root["pathGroups"]?.AsArray() ?? [])
        {
            var group = item?.AsObject() ??
                        throw new InvalidDataException(
                            "管理员 File 路径组格式错误。");
            var examples = group["examples"]?.AsArray()
                .Select(value => value?.GetValue<string>() ?? "")
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Take(3)
                .ToArray() ?? [];
            groups.Add(new FileHandlePathGroupSnapshot(
                group["pathGroup"]?.GetValue<string>() ?? "(unknown)",
                group["fileKind"]?.GetValue<string>() ?? "Unknown",
                group["sampleCount"]?.GetValue<int>() ?? 0,
                group["sampleSharePercent"]?.GetValue<double>() ?? 0,
                examples));
        }

        var filterInstances = new List<FileSystemFilterInstanceSnapshot>();
        foreach (var item in root["filterInstances"]?.AsArray() ?? [])
        {
            var instance = item?.AsObject() ??
                           throw new InvalidDataException(
                               "管理员 minifilter 实例格式错误。");
            filterInstances.Add(new FileSystemFilterInstanceSnapshot(
                instance["filterName"]?.GetValue<string>() ?? "",
                instance["volumeName"]?.GetValue<string>() ?? "",
                instance["altitude"]?.GetValue<string>() ?? "",
                instance["instanceName"]?.GetValue<string>() ?? "",
                instance["frame"]?.GetValue<string>() ?? "",
                instance["volumeStatus"]?.GetValue<string>() ?? ""));
            if (filterInstances.Count > 4096)
            {
                throw new InvalidDataException(
                    "管理员 minifilter 实例数量超出安全边界。");
            }
        }

        return new ElevatedFileHandleDiagnosticResult(
            new SystemFileHandleAttribution(
                total,
                requested,
                attempted,
                duplicated,
                resolved,
                false,
                summary)
            {
                RequiresKernelDriver = requiresKernelDriver,
                NativeErrorCode = nativeErrorCode
            },
            groups,
            filterInstances,
            root["debugPrivilegeEnabled"]?.GetValue<bool>() == true);
    }

    private static void WriteNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void EnsureNoReparsePoint(string path)
    {
        var current = File.Exists(path)
            ? new FileInfo(path).Directory
            : new DirectoryInfo(path);
        if (File.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "管理员诊断路径包含重解析点。");
        }
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "管理员诊断目录链包含重解析点。");
            }
            current = current.Parent;
        }
    }

    private static bool FixedHexEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(right.ToLowerInvariant()));

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
