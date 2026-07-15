using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Windows;

namespace MyPowerTools.Broker;

public sealed record AdbPortProxyPrecondition(
    string ListenAddress,
    int ListenPort,
    bool Exists,
    string ConnectAddress,
    int ConnectPort);

public static class AdbPortProxyPreState
{
    public static IReadOnlyList<AdbPortProxyPrecondition> Capture(
        IReadOnlyList<PortProxyRule> requestedRules,
        IReadOnlyList<PortProxyRule> currentRules)
    {
        var listeners = requestedRules
            .Select(rule => (Address: NormalizeAddress(rule.ListenAddress), rule.ListenPort))
            .Distinct()
            .OrderBy(listener => listener.Address, StringComparer.Ordinal)
            .ThenBy(listener => listener.ListenPort)
            .ToArray();
        if (listeners.Length != requestedRules.Count)
        {
            throw new InvalidOperationException("Each approval request must target a unique listen address and port.");
        }

        var result = new List<AdbPortProxyPrecondition>(listeners.Length);
        foreach (var listener in listeners)
        {
            var matches = currentRules.Where(rule =>
                    string.Equals(NormalizeAddress(rule.ListenAddress), listener.Address, StringComparison.Ordinal) &&
                    rule.ListenPort == listener.ListenPort)
                .ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidOperationException($"Portproxy returned duplicate rules for {listener.Address}:{listener.ListenPort}.");
            }

            var match = matches.SingleOrDefault();
            result.Add(match is null
                ? new AdbPortProxyPrecondition(listener.Address, listener.ListenPort, false, "", 0)
                : new AdbPortProxyPrecondition(
                    listener.Address,
                    listener.ListenPort,
                    true,
                    NormalizeAddress(match.ConnectAddress),
                    match.ConnectPort));
        }

        return result;
    }

    public static string Hash(IReadOnlyList<AdbPortProxyPrecondition> preconditions)
    {
        var canonical = new StringBuilder();
        foreach (var item in preconditions
                     .OrderBy(item => NormalizeAddress(item.ListenAddress), StringComparer.Ordinal)
                     .ThenBy(item => item.ListenPort))
        {
            canonical.Append(NormalizeAddress(item.ListenAddress))
                .Append('|')
                .Append(item.ListenPort.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(item.Exists ? '1' : '0')
                .Append('|')
                .Append(item.Exists ? NormalizeAddress(item.ConnectAddress) : "")
                .Append('|')
                .Append(item.Exists ? item.ConnectPort.ToString(CultureInfo.InvariantCulture) : "0")
                .Append('\n');
        }

        return Sha256(Encoding.UTF8.GetBytes(canonical.ToString()));
    }

    public static bool Equivalent(
        IReadOnlyList<AdbPortProxyPrecondition> left,
        IReadOnlyList<AdbPortProxyPrecondition> right)
    {
        return string.Equals(Hash(left), Hash(right), StringComparison.Ordinal);
    }

    public static string NormalizeAddress(string value)
    {
        var trimmed = value.Trim();
        return IPAddress.TryParse(trimmed, out var parsed)
            ? parsed.ToString().ToLowerInvariant()
            : trimmed.ToLowerInvariant();
    }

    internal static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public static class WindowsProtectedExecutable
{
    public static bool IsTrusted(string path, out string reason)
    {
        if (!IsProtectedLocation(path, out reason))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        try
        {
            using var writable = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            reason = "The elevated Broker executable is writable by the current standard process.";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
            reason = "The elevated Broker write protection could not be verified.";
            return false;
        }

        var protectedRoot = ProtectedRoots()
            .First(root => IsInside(root, fullPath));
        var directory = new DirectoryInfo(Path.GetDirectoryName(fullPath)!);
        while (directory is not null)
        {
            if (!DirectoryRejectsWriteProbe(directory.FullName, out reason))
            {
                return false;
            }
            if (string.Equals(directory.FullName, protectedRoot, StringComparison.OrdinalIgnoreCase))
            {
                reason = "";
                return true;
            }
            directory = directory.Parent;
        }

        reason = "The elevated Broker ACL chain did not reach its protected Program Files root.";
        return false;
    }

    public static bool IsProtectedLocation(string path, out string reason)
    {
        reason = "";
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            reason = "The elevated Broker path is missing or is not absolute.";
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var protectedRoots = ProtectedRoots();
        if (!protectedRoots.Any(root => IsInside(root, fullPath)))
        {
            reason = "The elevated Broker is outside an ACL-protected Program Files root.";
            return false;
        }

        if (ContainsReparsePoint(fullPath))
        {
            reason = "The elevated Broker path contains a reparse point.";
            return false;
        }
        return true;
    }

    public static bool ContainsReparsePoint(string path)
    {
        var current = File.Exists(path)
            ? new FileInfo(path).Directory
            : new DirectoryInfo(path);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
            current = current.Parent;
        }
        return false;
    }

    private static bool IsInside(string root, string path)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] ProtectedRoots() =>
    [
        .. new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }
        .Where(root => !string.IsNullOrWhiteSpace(root))
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    private static bool DirectoryRejectsWriteProbe(string directory, out string reason)
    {
        var probe = Path.Combine(directory, $".mpt-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using var created = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            reason = $"The elevated Broker ACL chain is writable at {directory}.";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "";
            return true;
        }
        catch (IOException)
        {
            reason = $"The elevated Broker directory protection could not be verified at {directory}.";
            return false;
        }
    }
}

public sealed record AdbPortProxyApprovalExecutionContext(
    INetworkBroker Network,
    string BrokerExecutablePath,
    bool BrokerPathTrusted,
    DateTimeOffset UtcNow)
{
    public static AdbPortProxyApprovalExecutionContext CreateDefault()
    {
        var executable = Path.GetFullPath(Environment.ProcessPath ?? "");
        var trusted = WindowsProtectedExecutable.IsProtectedLocation(executable, out _);
        INetworkBroker network = OperatingSystem.IsWindows()
            ? new WindowsPlatformPack().Network
            : new UnsupportedNetworkBroker("Windows portproxy", "Elevated portproxy approvals require Windows.");
        return new AdbPortProxyApprovalExecutionContext(
            network,
            executable,
            trusted,
            DateTimeOffset.UtcNow);
    }
}

public static class AdbPortProxyApprovalExecutor
{
    private const string ModuleId = "adb-forwarder";
    private const long MaximumRequestBytes = 64 * 1024;

    public static async Task<int> ExecuteAsync(
        string[] args,
        AuditLog audit,
        AdbPortProxyApprovalExecutionContext? executionContext = null)
    {
        var context = executionContext ?? AdbPortProxyApprovalExecutionContext.CreateDefault();
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("The elevated portproxy Broker requires Windows.");
            return 4;
        }
        if (!context.BrokerPathTrusted)
        {
            Console.WriteLine("The elevated Broker executable is outside the trusted ACL-protected release location.");
            return 4;
        }

        var requestPath = GetOption(args, "--request-file");
        var token = GetOption(args, "--token") ?? "";
        var expectedDigest = GetOption(args, "--digest") ?? "";
        var expectedBrokerHash = GetOption(args, "--broker-sha256") ?? "";
        if (string.IsNullOrWhiteSpace(requestPath) ||
            !IsHex(token, 32) ||
            !IsHex(expectedDigest, 64) ||
            !IsHex(expectedBrokerHash, 64))
        {
            Console.WriteLine("invalid approval request arguments");
            return 2;
        }

        var requestRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools",
            "broker-requests"));
        var fullPath = Path.GetFullPath(requestPath);
        var expectedPath = Path.Combine(requestRoot, $"{token}.json");
        if (!string.Equals(fullPath, expectedPath, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath) ||
            WindowsProtectedExecutable.ContainsReparsePoint(requestRoot) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            Console.WriteLine("approval request path failed canonical or reparse validation");
            return 2;
        }

        var claimPath = Path.Combine(requestRoot, $"{token}.{Environment.ProcessId}.{Guid.NewGuid():N}.claimed");
        try
        {
            File.Move(fullPath, claimPath, overwrite: false);
        }
        catch (IOException)
        {
            Console.WriteLine("approval request was already claimed or could not be claimed");
            return 3;
        }

        try
        {
            if ((File.GetAttributes(claimPath) & FileAttributes.ReparsePoint) != 0)
            {
                Console.WriteLine("claimed approval request is a reparse point");
                return 2;
            }

            string content;
            using (var stream = new FileStream(claimPath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                if (stream.Length is <= 0 or > MaximumRequestBytes)
                {
                    Console.WriteLine("approval request exceeds the bounded request size");
                    return 2;
                }
                content = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var actualDigest = Sha256Text(content);
            if (!FixedHexEquals(actualDigest, expectedDigest))
            {
                Console.WriteLine("approval request digest mismatch");
                return 2;
            }

            var root = JsonNode.Parse(content) as JsonObject;
            if (root is null ||
                root["schemaVersion"]?.GetValue<int>() != 2 ||
                !string.Equals(root["token"]?.GetValue<string>(), token, StringComparison.Ordinal) ||
                !string.Equals(root["moduleId"]?.GetValue<string>(), ModuleId, StringComparison.Ordinal))
            {
                Console.WriteLine("approval request failed schema or token validation");
                return 2;
            }

            if (!ValidateBrokerIdentity(root, context, expectedBrokerHash))
            {
                Console.WriteLine("approval request Broker identity mismatch");
                return 4;
            }
            if (!ValidateLifetime(root, context.UtcNow))
            {
                Console.WriteLine("approval request lifetime is invalid or expired");
                return 2;
            }

            var requestedRules = ParseRules(root["rules"] as JsonArray);
            var preconditions = ParsePreconditions(root["preconditions"] as JsonArray);
            var expectedPreStateHash = root["preStateSha256"]?.GetValue<string>() ?? "";
            if (!ValidateRules(requestedRules) ||
                preconditions.Count != requestedRules.Count ||
                !IsHex(expectedPreStateHash, 64) ||
                !FixedHexEquals(AdbPortProxyPreState.Hash(preconditions), expectedPreStateHash))
            {
                Console.WriteLine("approval request rules or pre-state are invalid");
                return 2;
            }

            var action = root["action"]?.GetValue<string>() ?? "";
            if (!string.Equals(action, "Ensure", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(action, "Remove", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("approval request action is invalid");
                return 2;
            }
            return await Task.Run(() => ExecuteChangesUnderNamedMutex(
                    context,
                    audit,
                    token,
                    action,
                    requestedRules,
                    preconditions,
                    expectedPreStateHash))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.WriteLine($"approval request failed: {ex.GetType().Name}");
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("elevated portproxy approval exceeded its execution deadline");
            return 1;
        }
        finally
        {
            TryDelete(claimPath);
        }
    }

    private static int ExecuteChangesUnderNamedMutex(
        AdbPortProxyApprovalExecutionContext context,
        AuditLog audit,
        string token,
        string action,
        IReadOnlyList<PortProxyRule> requestedRules,
        IReadOnlyList<AdbPortProxyPrecondition> preconditions,
        string expectedPreStateHash)
    {
        using var operationMutex = new Mutex(false, @"Global\MyPowerTools.AdbPortProxyApproval.v1");
        var acquired = false;
        try
        {
            try
            {
                acquired = operationMutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (!acquired)
            {
                Console.WriteLine("another elevated portproxy approval is still running");
                return 3;
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            return ExecuteChangesAsync(
                    context,
                    audit,
                    token,
                    action,
                    requestedRules,
                    preconditions,
                    expectedPreStateHash,
                    deadline.Token)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            if (acquired)
            {
                operationMutex.ReleaseMutex();
            }
        }
    }

    private static async Task<int> ExecuteChangesAsync(
        AdbPortProxyApprovalExecutionContext context,
        AuditLog audit,
        string token,
        string action,
        IReadOnlyList<PortProxyRule> requestedRules,
        IReadOnlyList<AdbPortProxyPrecondition> preconditions,
        string expectedPreStateHash,
        CancellationToken cancellationToken)
    {
        var current = await context.Network.ListPortProxyRulesAsync(cancellationToken).ConfigureAwait(false);
        var actualPreconditions = AdbPortProxyPreState.Capture(requestedRules, current);
        if (!FixedHexEquals(AdbPortProxyPreState.Hash(actualPreconditions), expectedPreStateHash) ||
            !AdbPortProxyPreState.Equivalent(actualPreconditions, preconditions))
        {
            Console.WriteLine("portproxy state changed after approval was staged");
            return 3;
        }

        var broker = new NetworkBroker(context.Network, audit);
        var reason = $"one-time approval token {token[..8]} prestate {expectedPreStateHash[..12]}";
        var rollback = new Stack<(string Action, PortProxyRule Rule)>();
        var changed = false;
        try
        {
            foreach (var requested in requestedRules)
            {
                var immediate = await context.Network.ListPortProxyRulesAsync(cancellationToken).ConfigureAwait(false);
                var listenerRules = immediate.Where(existing => SameListener(existing, requested)).ToArray();
                if (listenerRules.Length > 1)
                {
                    await RollbackAsync(broker, context.Network, rollback, reason).ConfigureAwait(false);
                    Console.WriteLine("portproxy returned duplicate listener state before a write");
                    return 3;
                }

                var listener = listenerRules.SingleOrDefault();
                BrokerOperationResult operation;
                if (string.Equals(action, "Ensure", StringComparison.OrdinalIgnoreCase))
                {
                    if (listener is not null && !SameRule(listener, requested))
                    {
                        await RollbackAsync(broker, context.Network, rollback, reason).ConfigureAwait(false);
                        Console.WriteLine("approval refused to overwrite an externally owned portproxy listener");
                        return 3;
                    }
                    if (listener is not null)
                    {
                        continue;
                    }

                    operation = await broker.ApplyAsync(ModuleId, requested, reason, cancellationToken).ConfigureAwait(false);
                    if (operation.Success)
                    {
                        rollback.Push(("remove", requested));
                        changed = true;
                    }
                }
                else
                {
                    var expected = preconditions.Single(item =>
                        string.Equals(
                            AdbPortProxyPreState.NormalizeAddress(item.ListenAddress),
                            AdbPortProxyPreState.NormalizeAddress(requested.ListenAddress),
                            StringComparison.Ordinal) &&
                        item.ListenPort == requested.ListenPort);
                    if (!expected.Exists)
                    {
                        if (listener is not null)
                        {
                            await RollbackAsync(broker, context.Network, rollback, reason).ConfigureAwait(false);
                            Console.WriteLine("portproxy listener appeared after approval was staged");
                            return 3;
                        }
                        continue;
                    }
                    if (listener is null || !SameRule(listener, requested))
                    {
                        await RollbackAsync(broker, context.Network, rollback, reason).ConfigureAwait(false);
                        Console.WriteLine("portproxy listener changed immediately before removal");
                        return 3;
                    }

                    operation = await broker.RemoveAsync(ModuleId, listener, reason, cancellationToken).ConfigureAwait(false);
                    if (operation.Success)
                    {
                        rollback.Push(("apply", listener));
                        changed = true;
                    }
                }

                if (!operation.Success)
                {
                    await RollbackAsync(broker, context.Network, rollback, reason).ConfigureAwait(false);
                    Console.WriteLine($"{operation.State}: {operation.Message}");
                    return 1;
                }

                var afterItem = await context.Network.ListPortProxyRulesAsync(cancellationToken).ConfigureAwait(false);
                var itemVerified = string.Equals(action, "Ensure", StringComparison.OrdinalIgnoreCase)
                    ? afterItem.Any(existing => SameRule(existing, requested))
                    : afterItem.All(existing => !SameListener(existing, requested));
                if (!itemVerified)
                {
                    await RollbackAsync(broker, context.Network, rollback, reason).ConfigureAwait(false);
                    Console.WriteLine("portproxy state changed during an approved operation");
                    return 1;
                }
            }

            var final = await context.Network.ListPortProxyRulesAsync(cancellationToken).ConfigureAwait(false);
            var finalMatches = string.Equals(action, "Ensure", StringComparison.OrdinalIgnoreCase)
                ? requestedRules.All(requested => final.Any(existing => SameRule(existing, requested)))
                : requestedRules.All(requested => final.All(existing => !SameListener(existing, requested)));
            if (!finalMatches)
            {
                await RollbackAsync(broker, context.Network, rollback, reason).ConfigureAwait(false);
                Console.WriteLine("portproxy final-state verification failed");
                return 1;
            }

            Console.WriteLine(changed
                ? $"success: applied {requestedRules.Count} approved listener operation(s)"
                : "noop: approved listener state was already satisfied");
            return changed ? 0 : 10;
        }
        catch (OperationCanceledException)
        {
            await RollbackAsync(broker, context.Network, rollback, reason).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            await RollbackAsync(broker, context.Network, rollback, reason).ConfigureAwait(false);
            Console.WriteLine($"portproxy operation failed; rollback was attempted: {ex.GetType().Name}");
            return 1;
        }
    }

    private static async Task RollbackAsync(
        NetworkBroker broker,
        INetworkBroker network,
        Stack<(string Action, PortProxyRule Rule)> rollback,
        string reason)
    {
        using var rollbackDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (rollback.TryPop(out var item))
            {
                var current = await network.ListPortProxyRulesAsync(rollbackDeadline.Token).ConfigureAwait(false);
                var listeners = current.Where(existing => SameListener(existing, item.Rule)).ToArray();
                if (listeners.Length > 1)
                {
                    Console.WriteLine("rollback-incomplete: duplicate listener state");
                    continue;
                }
                var listener = listeners.SingleOrDefault();
                if (string.Equals(item.Action, "remove", StringComparison.Ordinal))
                {
                    if (listener is not null && SameRule(listener, item.Rule))
                    {
                        var result = await broker.RemoveAsync(ModuleId, item.Rule, $"rollback {reason}", rollbackDeadline.Token).ConfigureAwait(false);
                        if (!result.Success)
                        {
                            Console.WriteLine($"rollback-incomplete: {result.State}");
                        }
                    }
                }
                else if (listener is null)
                {
                    var result = await broker.ApplyAsync(ModuleId, item.Rule, $"rollback {reason}", rollbackDeadline.Token).ConfigureAwait(false);
                    if (!result.Success)
                    {
                        Console.WriteLine($"rollback-incomplete: {result.State}");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            Console.WriteLine($"rollback-incomplete: {ex.GetType().Name}");
        }
    }

    private static bool ValidateBrokerIdentity(
        JsonObject root,
        AdbPortProxyApprovalExecutionContext context,
        string expectedBrokerHash)
    {
        var broker = root["broker"] as JsonObject;
        var path = broker?["path"]?.GetValue<string>() ?? "";
        var hash = broker?["sha256"]?.GetValue<string>() ?? "";
        if (!Path.IsPathFullyQualified(path) || !IsHex(hash, 64))
        {
            return false;
        }

        var actualPath = Path.GetFullPath(context.BrokerExecutablePath);
        return string.Equals(Path.GetFullPath(path), actualPath, StringComparison.OrdinalIgnoreCase) &&
               FixedHexEquals(hash, expectedBrokerHash) &&
               FixedHexEquals(AdbPortProxyPreState.Sha256File(actualPath), hash);
    }

    private static bool ValidateLifetime(JsonObject root, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(root["createdAt"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt) ||
            !DateTimeOffset.TryParse(root["expiresAt"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt))
        {
            return false;
        }

        var lifetime = expiresAt - createdAt;
        return createdAt <= now.AddSeconds(30) &&
               createdAt >= now.AddMinutes(-6) &&
               lifetime > TimeSpan.Zero &&
               lifetime <= TimeSpan.FromMinutes(5) &&
               expiresAt > now;
    }

    private static IReadOnlyList<PortProxyRule> ParseRules(JsonArray? array)
    {
        return array?.OfType<JsonObject>()
            .Select(item => new PortProxyRule(
                item["listenAddress"]?.GetValue<string>() ?? "",
                item["listenPort"]?.GetValue<int>() ?? 0,
                item["connectAddress"]?.GetValue<string>() ?? "",
                item["connectPort"]?.GetValue<int>() ?? 0))
            .ToArray() ?? [];
    }

    private static IReadOnlyList<AdbPortProxyPrecondition> ParsePreconditions(JsonArray? array)
    {
        return array?.OfType<JsonObject>()
            .Select(item => new AdbPortProxyPrecondition(
                item["listenAddress"]?.GetValue<string>() ?? "",
                item["listenPort"]?.GetValue<int>() ?? 0,
                item["exists"]?.GetValue<bool>() ?? false,
                item["connectAddress"]?.GetValue<string>() ?? "",
                item["connectPort"]?.GetValue<int>() ?? 0))
            .ToArray() ?? [];
    }

    private static bool ValidateRules(IReadOnlyList<PortProxyRule> rules)
    {
        return rules.Count > 0 &&
               rules.Count <= 4 &&
               rules.All(rule =>
                   !string.IsNullOrWhiteSpace(rule.ListenAddress) &&
                   !string.IsNullOrWhiteSpace(rule.ConnectAddress) &&
                   rule.ListenPort is >= 1 and <= 65535 &&
                   rule.ConnectPort is >= 1 and <= 65535) &&
               rules.Select(rule => $"{AdbPortProxyPreState.NormalizeAddress(rule.ListenAddress)}:{rule.ListenPort}")
                   .Distinct(StringComparer.Ordinal)
                   .Count() == rules.Count;
    }

    private static bool SameListener(PortProxyRule left, PortProxyRule right) =>
        string.Equals(
            AdbPortProxyPreState.NormalizeAddress(left.ListenAddress),
            AdbPortProxyPreState.NormalizeAddress(right.ListenAddress),
            StringComparison.Ordinal) &&
        left.ListenPort == right.ListenPort;

    private static bool SameRule(PortProxyRule left, PortProxyRule right) =>
        SameListener(left, right) &&
        string.Equals(
            AdbPortProxyPreState.NormalizeAddress(left.ConnectAddress),
            AdbPortProxyPreState.NormalizeAddress(right.ConnectAddress),
            StringComparison.Ordinal) &&
        left.ConnectPort == right.ConnectPort;

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool IsHex(string value, int length) =>
        value.Length == length && value.All(Uri.IsHexDigit);

    private static string Sha256Text(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static bool FixedHexEquals(string left, string right)
    {
        return left.Length == right.Length &&
               CryptographicOperations.FixedTimeEquals(
                   Encoding.ASCII.GetBytes(left.ToLowerInvariant()),
                   Encoding.ASCII.GetBytes(right.ToLowerInvariant()));
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
