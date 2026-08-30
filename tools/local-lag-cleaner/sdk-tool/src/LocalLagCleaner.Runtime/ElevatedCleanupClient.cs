using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalLagCleaner.MyPowerTools;

namespace LocalLagCleaner.Runtime;

internal static class ElevatedCleanupClient
{
    private const string ModuleId = "local-lag-cleaner";
    private const string ActionId = "service-restart";
    private const int MaximumRequestBytes = 64 * 1024;
    private const int MaximumResultBytes = 256 * 1024;

    public static async Task<CleanupExecutionResult> RunAsync(
        string stateDirectory,
        string planId,
        CleanupAction expectedAction,
        string confirmationToken,
        bool allowDisconnect,
        bool allowServiceRestart,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "管理员服务重启仅支持 Windows。");
        }

        if (!IsServiceAction(expectedAction))
        {
            throw new InvalidOperationException(
                "管理员 Broker 只接受 Windows 服务重启计划。");
        }

        ValidateIdentifier(planId, 32, "planId");
        ValidateIdentifier(confirmationToken, 8, "confirmationToken");

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

        var requestDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools",
            "broker-requests",
            ModuleId));
        Directory.CreateDirectory(requestDirectory);
        EnsureNoReparsePoint(requestDirectory);

        var requestToken = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var requestPath = Path.Combine(requestDirectory, $"{requestToken}.json");
        var resultPath = Path.Combine(requestDirectory, $"{requestToken}.result.json");
        var now = DateTimeOffset.UtcNow;
        var request = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["token"] = requestToken,
            ["moduleId"] = ModuleId,
            ["action"] = ActionId,
            ["createdAt"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["expiresAt"] = now.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture),
            ["stateDirectory"] = Path.GetFullPath(stateDirectory),
            ["planId"] = planId,
            ["expectedAction"] = expectedAction.ToString(),
            ["confirmationToken"] = confirmationToken.Trim().ToUpperInvariant(),
            ["allowDisconnect"] = allowDisconnect,
            ["allowServiceRestart"] = allowServiceRestart,
            ["broker"] = new JsonObject
            {
                ["path"] = brokerPath,
                ["sha256"] = brokerHash
            }
        };
        var requestBytes = Encoding.UTF8.GetBytes(
            request.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (requestBytes.Length > MaximumRequestBytes)
        {
            throw new InvalidDataException("管理员服务重启请求超过大小上限。");
        }

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
                         "cleanup",
                         "local-lag-cleaner",
                         "--request-file",
                         requestPath,
                         "--token",
                         requestToken,
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
                throw new TimeoutException("管理员服务重启在三分钟内没有完成。");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"管理员 Broker 拒绝或未完成服务重启（退出码 {process.ExitCode}）。");
            }

            return await ReadResultAsync(
                    resultPath,
                    requestToken,
                    requestDigest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    private static bool IsServiceAction(CleanupAction action) =>
        action is CleanupAction.DeliveryOptimization or
            CleanupAction.NvidiaContainer or
            CleanupAction.RemoteDesktop or
            CleanupAction.WindowsSearch;

    private static void ValidateIdentifier(string value, int length, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != length ||
            !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"{name} 格式无效。");
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

    private static async Task<CleanupExecutionResult> ReadResultAsync(
        string path,
        string requestToken,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "管理员 Broker 没有生成可信的服务重启结果。");
        }

        var information = new FileInfo(path);
        if (information.Length is <= 0 or > MaximumResultBytes)
        {
            throw new InvalidDataException(
                "管理员服务重启结果超出安全边界。");
        }

        var json = await File.ReadAllTextAsync(
                path,
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
        var root = JsonNode.Parse(
                       json,
                       documentOptions: new JsonDocumentOptions
                       {
                           AllowTrailingCommas = false,
                           CommentHandling = JsonCommentHandling.Disallow,
                           MaxDepth = 12
                       })?.AsObject() ??
                   throw new InvalidDataException(
                       "管理员服务重启结果格式错误。");
        if (root.Count != 4 ||
            root["schemaVersion"]?.GetValue<int>() != 1 ||
            !string.Equals(root["token"]?.GetValue<string>(), requestToken, StringComparison.Ordinal) ||
            !FixedHexEquals(root["requestDigest"]?.GetValue<string>() ?? "", requestDigest))
        {
            throw new InvalidDataException(
                "管理员服务重启结果与当前请求不匹配。");
        }

        return root["result"]?.Deserialize<CleanupExecutionResult>(LagCleanerJson.Compact) ??
               throw new InvalidDataException(
                   "管理员服务重启结果缺少执行结果。");
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
            throw new InvalidOperationException("管理员服务重启路径包含重解析点。");
        }

        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("管理员服务重启目录链包含重解析点。");
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
