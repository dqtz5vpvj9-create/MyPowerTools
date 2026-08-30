using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalLagCleaner.MyPowerTools;
using MyPowerTools.Broker;

namespace MyPowerTools.ElevatedBroker;

internal static class LocalLagCleanerCleanupExecutor
{
    private const string ModuleId = "local-lag-cleaner";
    private const string ActionId = "service-restart";
    private const int MaximumRequestBytes = 64 * 1024;
    private const int MaximumResultBytes = 256 * 1024;

    public static async Task<int> ExecuteAsync(
        string[] arguments,
        AuditLog audit,
        CancellationToken cancellationToken = default)
    {
        var requestToken = GetOption(arguments, "--token") ?? "";
        var requestPath = GetOption(arguments, "--request-file") ?? "";
        var requestDigest = GetOption(arguments, "--digest") ?? "";
        var expectedBrokerHash = GetOption(arguments, "--broker-sha256") ?? "";
        var auditId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        CleanupRequest? request = null;

        try
        {
            request = ReadAndValidateRequest(
                requestPath,
                requestToken,
                requestDigest,
                expectedBrokerHash);
            using var operationGate = new Semaphore(
                initialCount: 1,
                maximumCount: 1,
                "Global\\MyPowerTools.LocalLagCleanerCleanup.v1");
            if (!operationGate.WaitOne(TimeSpan.FromSeconds(10)))
            {
                AppendAudit(audit, auditId, request, "busy", "another service restart is active");
                return 4;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cleanup = new CleanupCoordinator(request.StateDirectory);
                var result = await cleanup.ApplyPendingPlanAsync(
                        request.PlanId,
                        request.ExpectedAction,
                        request.ConfirmationToken,
                        request.AllowDisconnect,
                        request.AllowServiceRestart,
                        cancellationToken)
                    .ConfigureAwait(false);
                await WriteResultAsync(
                        request.ResultPath,
                        requestToken,
                        requestDigest,
                        result,
                        cancellationToken)
                    .ConfigureAwait(false);
                AppendAudit(
                    audit,
                    auditId,
                    request,
                    result.Succeeded ? "succeeded" : "failed",
                    result.Items.FirstOrDefault()?.Message ?? "service restart completed");
                return 0;
            }
            finally
            {
                operationGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            if (request is not null)
            {
                AppendAudit(audit, auditId, request, "cancelled", "service restart cancelled");
            }

            return 5;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException or
            Win32Exception or
            JsonException or
            OverflowException)
        {
            if (request is not null)
            {
                AppendAudit(audit, auditId, request, "rejected", exception.GetType().Name);
            }

            return 3;
        }
    }

    private static CleanupRequest ReadAndValidateRequest(
        string requestPath,
        string requestToken,
        string requestDigest,
        string expectedBrokerHash)
    {
        if (!OperatingSystem.IsWindows() ||
            !IsHex(requestToken, 32) ||
            !IsHex(requestDigest, 64) ||
            !IsHex(expectedBrokerHash, 64) ||
            !Path.IsPathFullyQualified(requestPath))
        {
            throw new InvalidDataException("管理员服务重启请求参数无效。");
        }

        var expectedDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools",
            "broker-requests",
            ModuleId));
        var fullRequestPath = Path.GetFullPath(requestPath);
        if (!string.Equals(
                Path.GetDirectoryName(fullRequestPath),
                expectedDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFileName(fullRequestPath),
                $"{requestToken}.json",
                StringComparison.OrdinalIgnoreCase) ||
            WindowsProtectedExecutable.ContainsReparsePoint(fullRequestPath))
        {
            throw new InvalidDataException("管理员服务重启请求路径不在固定目录中。");
        }

        var bytes = File.ReadAllBytes(fullRequestPath);
        if (bytes.Length is 0 or > MaximumRequestBytes ||
            !FixedHexEquals(Sha256(bytes), requestDigest))
        {
            throw new InvalidDataException("管理员服务重启请求摘要无效。");
        }

        var root = JsonNode.Parse(
                       bytes,
                       documentOptions: new JsonDocumentOptions
                       {
                           AllowTrailingCommas = false,
                           CommentHandling = JsonCommentHandling.Disallow,
                           MaxDepth = 8
                       })?.AsObject() ??
                   throw new InvalidDataException("管理员服务重启请求根节点无效。");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion",
            "token",
            "moduleId",
            "action",
            "createdAt",
            "expiresAt",
            "stateDirectory",
            "planId",
            "expectedAction",
            "confirmationToken",
            "allowDisconnect",
            "allowServiceRestart",
            "broker"
        };
        if (root.Count != allowed.Count ||
            root.Any(property => !allowed.Contains(property.Key)) ||
            root["schemaVersion"]?.GetValue<int>() != 1 ||
            !string.Equals(root["token"]?.GetValue<string>(), requestToken, StringComparison.Ordinal) ||
            !string.Equals(root["moduleId"]?.GetValue<string>(), ModuleId, StringComparison.Ordinal) ||
            !string.Equals(root["action"]?.GetValue<string>(), ActionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("管理员服务重启请求字段无效。");
        }

        if (!DateTimeOffset.TryParse(
                root["createdAt"]?.GetValue<string>(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var createdAt) ||
            !DateTimeOffset.TryParse(
                root["expiresAt"]?.GetValue<string>(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt))
        {
            throw new InvalidDataException("管理员服务重启请求有效期无效。");
        }

        var now = DateTimeOffset.UtcNow;
        if (createdAt > now.AddSeconds(30) ||
            createdAt < now.AddMinutes(-6) ||
            expiresAt <= now ||
            expiresAt - createdAt > TimeSpan.FromMinutes(5))
        {
            throw new InvalidDataException("管理员服务重启请求已过期。");
        }

        var stateDirectory = root["stateDirectory"]?.GetValue<string>() ?? "";
        var expectedStateDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools",
            "state",
            "tools",
            ModuleId));
        if (!Path.IsPathFullyQualified(stateDirectory) ||
            !string.Equals(
                Path.GetFullPath(stateDirectory),
                expectedStateDirectory,
                StringComparison.OrdinalIgnoreCase) ||
            WindowsProtectedExecutable.ContainsReparsePoint(stateDirectory))
        {
            throw new InvalidDataException("管理员服务重启状态目录无效。");
        }

        var planId = root["planId"]?.GetValue<string>() ?? "";
        var confirmationToken = root["confirmationToken"]?.GetValue<string>() ?? "";
        if (!IsHex(planId, 32) || !IsHex(confirmationToken, 8))
        {
            throw new InvalidDataException("管理员服务重启计划身份无效。");
        }

        if (!Enum.TryParse<CleanupAction>(
                root["expectedAction"]?.GetValue<string>(),
                ignoreCase: false,
                out var expectedAction) ||
            !Enum.IsDefined(expectedAction) ||
            !IsServiceAction(expectedAction))
        {
            throw new InvalidDataException("管理员服务重启动作无效。");
        }

        var broker = root["broker"] as JsonObject ??
                     throw new InvalidDataException("管理员服务重启 Broker 身份缺失。");
        if (broker.Count != 2 ||
            broker.Any(property => property.Key is not ("path" or "sha256")))
        {
            throw new InvalidDataException("管理员服务重启 Broker 身份字段无效。");
        }

        var executablePath = Path.GetFullPath(Environment.ProcessPath ?? "");
        var approvedPath = broker["path"]?.GetValue<string>() ?? "";
        var approvedHash = broker["sha256"]?.GetValue<string>() ?? "";
        if (!Path.IsPathFullyQualified(approvedPath) ||
            !string.Equals(
                Path.GetFullPath(approvedPath),
                executablePath,
                StringComparison.OrdinalIgnoreCase) ||
            !FixedHexEquals(approvedHash, expectedBrokerHash) ||
            !FixedHexEquals(Sha256File(executablePath), expectedBrokerHash))
        {
            throw new InvalidDataException("管理员服务重启 Broker 身份发生变化。");
        }

        return new CleanupRequest(
            Path.Combine(expectedDirectory, $"{requestToken}.result.json"),
            Path.GetFullPath(stateDirectory),
            planId,
            expectedAction,
            confirmationToken.ToUpperInvariant(),
            root["allowDisconnect"]?.GetValue<bool>() == true,
            root["allowServiceRestart"]?.GetValue<bool>() == true);
    }

    private static async Task WriteResultAsync(
        string path,
        string requestToken,
        string requestDigest,
        CleanupExecutionResult result,
        CancellationToken cancellationToken)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["token"] = requestToken,
            ["requestDigest"] = requestDigest,
            ["result"] = JsonSerializer.SerializeToNode(result, LagCleanerJson.Compact)
        };
        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString(LagCleanerJson.Compact));
        if (bytes.Length > MaximumResultBytes)
        {
            throw new InvalidDataException("管理员服务重启结果超过大小上限。");
        }

        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AppendAudit(
        AuditLog audit,
        string auditId,
        CleanupRequest request,
        string result,
        string detail)
    {
        audit.Append(new BrokerAuditEntry(
            auditId,
            DateTimeOffset.UtcNow,
            ModuleId,
            ActionId,
            "elevated",
            $"plan={request.PlanId}",
            "one-time approved Windows service restart",
            true,
            result,
            detail));
    }

    private static string? GetOption(string[] arguments, string name)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static bool IsServiceAction(CleanupAction action) =>
        action is CleanupAction.DeliveryOptimization or
            CleanupAction.NvidiaContainer or
            CleanupAction.RemoteDesktop or
            CleanupAction.WindowsSearch;

    private static bool IsHex(string value, int length) =>
        value.Length == length && value.All(Uri.IsHexDigit);

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Sha256(stream);
    }

    private static string Sha256(Stream stream) =>
        Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

    private static bool FixedHexEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(right.ToLowerInvariant()));

    private sealed record CleanupRequest(
        string ResultPath,
        string StateDirectory,
        string PlanId,
        CleanupAction ExpectedAction,
        string ConfirmationToken,
        bool AllowDisconnect,
        bool AllowServiceRestart);
}
