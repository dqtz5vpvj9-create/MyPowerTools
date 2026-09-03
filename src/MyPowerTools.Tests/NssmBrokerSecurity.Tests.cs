using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Broker;
using NssmManager.Runtime;

namespace MyPowerTools.Tests;

/// <summary>
/// Security tests for the elevated NSSM path: the request envelope the Elevated Broker accepts,
/// the request/result digest binding the unprivileged client enforces, and the per-operation
/// argument allowlist that keeps one approved operation from carrying another one's payload.
///
/// NssmServiceApprovalExecutor is internal to MyPowerTools.ElevatedBroker, which the test project
/// references with ReferenceOutputAssembly="false" because the Broker is a self-contained WinExe.
/// Its validation gates are therefore reached by reflection over the built assembly.
///
/// The ProtectedFileStaging.AlreadyMatches primitive itself is covered by ProtectedFileStagingTests
/// in ServiceUnitSupervision.Tests.cs. This file only covers the part that test cannot see: that
/// the NSSM executor actually consults the short-circuit before it tries to replace an
/// image-locked nssm-manager.exe.
/// </summary>
public sealed class NssmBrokerSecurityTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly Assembly ElevatedBroker = LoadElevatedBrokerAssembly();
    private static readonly Type Executor = ElevatedBroker.GetType(
        "MyPowerTools.ElevatedBroker.NssmServiceApprovalExecutor",
        throwOnError: true)!;

    private static readonly string BrokerProcessPath = Path.GetFullPath(
        Environment.ProcessPath ?? throw new InvalidOperationException("The host process path is unavailable."));

    private static readonly string BrokerProcessHash = HashFile(BrokerProcessPath);

    private static readonly (string Operation, string[] Allowed)[] OperationAllowlist =
    [
        ("nssm-manager.install", ["configuration", "executablePath", "passwordPipe"]),
        ("nssm-manager.apply", ["configuration", "expectedImagePath", "passwordPipe"]),
        ("nssm-manager.remove", ["serviceName", "expectedImagePath"]),
        ("nssm-manager.control", ["serviceName", "action", "startArguments", "expectedImagePath"]),
        ("nssm-manager.migrate", ["serviceName", "executablePath", "expectedImagePath"]),
        ("nssm-manager.registry-set", ["serviceName", "parameter", "subparameter", "values", "expectedImagePath", "passwordPipe"]),
        ("nssm-manager.registry-reset", ["serviceName", "parameter", "subparameter", "expectedImagePath"]),
        ("nssm-manager.imagepath", ["serviceName", "imagePath", "expectedImagePath"]),
        ("nssm-manager.rollback", ["serviceName", "expectedImagePath"])
    ];

    [Fact]
    public void Request_envelope_binds_the_exact_field_set_the_token_and_the_module()
    {
        var token = NewToken();

        ValidateRoot(Envelope(token), token);

        var extra = Envelope(token);
        extra["reason"] = "please elevate";
        Rejected(extra, token, "Request fields are invalid.");

        var renamed = Envelope(token);
        renamed.Remove("moduleId");
        renamed["module_id"] = "nssm-manager";
        Rejected(renamed, token, "Request fields are invalid.");

        var missing = Envelope(token);
        missing.Remove("arguments");
        Rejected(missing, token, "Request fields are invalid.");

        Rejected(Envelope(token, schemaVersion: 2), token, "Request identity is invalid.");
        Rejected(Envelope(token, moduleId: "adb-forwarder"), token, "Request identity is invalid.");
        Rejected(Envelope(NewToken()), token, "Request identity is invalid.");

        var textArguments = Envelope(token);
        textArguments["arguments"] = "serviceName=mpt-test";
        Rejected(textArguments, token, "Arguments must be an object.");
    }

    [Fact]
    public void Only_the_nine_declared_operations_are_accepted()
    {
        var token = NewToken();

        foreach (var (operation, _) in OperationAllowlist)
        {
            ValidateRoot(Envelope(token, operation: operation), token);
        }

        foreach (var operation in new[] { "", "nssm-manager.uninstall", "NSSM-MANAGER.REMOVE", "nssm-manager.remove ", "portproxy.apply" })
        {
            Rejected(Envelope(token, operation: operation), token, "Operation is not allowed.");
        }
    }

    [Fact]
    public void Request_lifetime_is_bounded_and_rejects_expired_and_future_dated_approvals()
    {
        var token = NewToken();
        var now = DateTimeOffset.UtcNow;

        ValidateRoot(Envelope(token, createdAt: now, expiresAt: now.AddMinutes(5)), token);
        ValidateRoot(Envelope(token, createdAt: now.AddMinutes(-4), expiresAt: now.AddMinutes(1)), token);

        Rejected(Envelope(token, createdAt: now.AddMinutes(-8), expiresAt: now.AddMinutes(-3)), token, "Request lifetime is invalid.");
        Rejected(Envelope(token, createdAt: now.AddMinutes(-5), expiresAt: now), token, "Request lifetime is invalid.");
        Rejected(Envelope(token, createdAt: now.AddMinutes(5), expiresAt: now.AddMinutes(9)), token, "Request lifetime is invalid.");
        Rejected(Envelope(token, createdAt: now, expiresAt: now.AddMinutes(6)), token, "Request lifetime is invalid.");
        Rejected(Envelope(token, createdAt: now.AddMinutes(-10), expiresAt: now.AddMinutes(1)), token, "Request lifetime is invalid.");

        var unparsable = Envelope(token);
        unparsable["expiresAt"] = "soon";
        Rejected(unparsable, token, "Request lifetime is invalid.");
    }

    [Fact]
    public void Envelope_broker_identity_must_name_the_running_broker_and_its_command_line_hash()
    {
        var token = NewToken();

        var otherHash = new string('a', 64);
        Rejected(Envelope(token, brokerHash: otherHash), token, "Broker identity is invalid.");
        Rejected(Envelope(token), token, "Broker identity is invalid.", commandLineHash: otherHash);

        var elsewhere = Envelope(token);
        elsewhere["broker"] = new JsonObject
        {
            ["path"] = Path.Combine(Path.GetTempPath(), "MyPowerTools.ElevatedBroker.exe"),
            ["sha256"] = BrokerProcessHash
        };
        Rejected(elsewhere, token, "Broker identity is invalid.");

        var padded = Envelope(token);
        padded["broker"] = new JsonObject
        {
            ["path"] = BrokerProcessPath,
            ["sha256"] = BrokerProcessHash,
            ["signer"] = "MyPowerTools"
        };
        Rejected(padded, token, "Broker identity is invalid.");

        var absent = Envelope(token);
        absent["broker"] = null;
        Rejected(absent, token, "Broker identity is missing.");
    }

    [Fact]
    public void Broker_self_hash_mismatch_is_rejected()
    {
        Invoke("VerifyBroker", BrokerProcessHash);

        var mismatch = Assert.Throws<InvalidDataException>(() => Invoke("VerifyBroker", new string('b', 64)));

        Assert.Equal("Broker hash mismatch.", mismatch.Message);
    }

    [Fact]
    public async Task Malformed_broker_command_line_is_rejected_before_any_request_is_read()
    {
        var audit = NewAuditLog();
        var requestPath = Path.Combine(NewDirectory(), NewToken() + ".json");
        string[] wellFormed =
        [
            "--request-file", requestPath,
            "--token", NewToken(),
            "--digest", new string('c', 64),
            "--broker-sha256", new string('d', 64)
        ];

        Assert.Equal(2, await ExecuteAsync([], audit));
        Assert.Equal(2, await ExecuteAsync(["--token", NewToken()], audit));
        Assert.Equal(2, await ExecuteAsync(WithOption(wellFormed, "--token", "short"), audit));
        Assert.Equal(2, await ExecuteAsync(WithOption(wellFormed, "--token", new string('z', 32)), audit));
        Assert.Equal(2, await ExecuteAsync(WithOption(wellFormed, "--digest", new string('c', 63)), audit));
        Assert.Equal(2, await ExecuteAsync(WithOption(wellFormed, "--broker-sha256", ""), audit));
        Assert.False(File.Exists(requestPath));
    }

    [Fact]
    public async Task Oversized_request_is_rejected_before_json_parsing()
    {
        if (!OperatingSystem.IsWindows())
        {
            // ValidateRequestPath/ValidateCallerIdentity run before the size gate and both throw
            // PlatformNotSupportedException off Windows, so the size gate is unreachable here.
            return;
        }

        var request = StageRequest(Envelope(NewToken()), oversized: true);
        var result = await ExecuteAsync(request.Arguments, request.Audit);

        Assert.Equal(5, result);
        Assert.Equal(
            typeof(InvalidDataException).FullName,
            ReadResultFile(request.ResultPath)["payload"]!["exceptionType"]!.GetValue<string>());
    }

    [Fact]
    public void Every_operation_accepts_only_its_own_argument_names()
    {
        var everyName = OperationAllowlist.SelectMany(item => item.Allowed).Distinct(StringComparer.Ordinal).ToArray();

        foreach (var (operation, allowed) in OperationAllowlist)
        {
            var accepted = new JsonObject();
            foreach (var name in allowed)
            {
                accepted[name] = "value";
            }
            ValidateArguments(operation, accepted);

            foreach (var foreign in everyName.Where(name => !allowed.Contains(name, StringComparer.Ordinal)))
            {
                var leaked = accepted.DeepClone().AsObject();
                leaked[foreign] = "value";
                var rejected = Assert.Throws<InvalidDataException>(() => ValidateArguments(operation, leaked));
                Assert.Equal($"Argument '{foreign}' is not allowed for {operation}.", rejected.Message);
            }

            var invented = accepted.DeepClone().AsObject();
            invented["--exec"] = "cmd.exe";
            Assert.Throws<InvalidDataException>(() => ValidateArguments(operation, invented));

            var recased = new JsonObject { [allowed[0].ToUpperInvariant()] = "value" };
            Assert.Throws<InvalidDataException>(() => ValidateArguments(operation, recased));
        }

        var unknownOperation = Assert.Throws<InvalidDataException>(
            () => ValidateArguments("nssm-manager.uninstall", new JsonObject()));
        Assert.Equal("Operation is not allowed.", unknownOperation.Message);
    }

    [Fact]
    public void Trusted_service_image_requires_an_acl_protected_location()
    {
        var directory = NewDirectory();
        var imagePath = Path.Combine(directory, "service-host.exe");
        File.WriteAllBytes(imagePath, [0x4D, 0x5A, 0x90, 0x00]);

        Assert.False(WindowsProtectedExecutable.IsProtectedLocation(imagePath, out var reason));
        Assert.NotEmpty(reason);

        if (!OperatingSystem.IsWindows())
        {
            // TrustedServiceImage splits the ImagePath with shell32!CommandLineToArgvW before it
            // reaches the ACL gate, so the method itself cannot be called off Windows.
            return;
        }

        var arguments = new JsonObject { ["imagePath"] = "\"" + imagePath + "\" --run" };
        var rejected = Assert.Throws<InvalidDataException>(() => Invoke("TrustedServiceImage", arguments));
        Assert.Contains("not ACL-protected", rejected.Message, StringComparison.Ordinal);

        var absent = new JsonObject { ["imagePath"] = Path.Combine(directory, "missing.exe") };
        Assert.Throws<InvalidDataException>(() => Invoke("TrustedServiceImage", absent));

        var empty = new JsonObject { ["imagePath"] = "   " };
        Assert.Throws<InvalidDataException>(() => Invoke("TrustedServiceImage", empty));
    }

    [Fact]
    public void Protected_executable_staging_short_circuits_an_image_locked_host()
    {
        // The AlreadyMatches primitive is owned by ProtectedFileStagingTests in
        // ServiceUnitSupervision.Tests.cs. What only the executor can prove is that the
        // short-circuit sits ahead of the stage-and-replace, and that it still revalidates the
        // ACLs of the copy it decides to keep.
        var source = File.ReadAllText(Path.Combine(
            Root, "src", "MyPowerTools.ElevatedBroker", "NssmServiceApprovalExecutor.cs"));
        var materialize = Between(source, "private static string MaterializeProtectedExecutable", "private static void ProtectDirectory");
        var shortCircuit = materialize.IndexOf("ProtectedFileStaging.AlreadyMatches(destinationPath, sourceHash)", StringComparison.Ordinal);
        var replace = materialize.IndexOf("File.Move(temporaryPath, destinationPath, true)", StringComparison.Ordinal);

        var keptCopy = materialize.IndexOf("ValidateProtectedExecutable(destinationPath, destinationDirectory)", shortCircuit + 1, StringComparison.Ordinal);

        Assert.True(shortCircuit >= 0, "The NSSM executor must consult ProtectedFileStaging before replacing the host.");
        Assert.True(replace > shortCircuit, "The staging short-circuit must precede the replace that a running service blocks.");
        Assert.True(keptCopy > shortCircuit && keptCopy < replace, "The kept copy must still be ACL-validated before it is returned.");
        Assert.Contains("WindowsProtectedExecutable.IsProtectedLocation(path, out var reason)", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Broker_result_is_accepted_only_by_the_request_that_produced_it()
    {
        var directory = NewDirectory();
        var token = NewToken();
        var digest = new string('1', 64);
        var resultPath = Path.Combine(directory, token + ".result.json");
        WriteResult(resultPath, token, digest, true, "completed", new JsonObject { ["removed"] = "mpt-test" });

        var payload = await ReadResultAsync(resultPath, token, digest, "nssm-manager.remove");
        Assert.Equal("mpt-test", payload["removed"]!.GetValue<string>());

        await RejectsResult(resultPath, NewToken(), digest);
        await RejectsResult(resultPath, token, new string('2', 64));
        await RejectsResult(resultPath, token, digest.ToUpperInvariant()[..63] + "0");
    }

    [Fact]
    public async Task A_result_left_behind_by_another_request_cannot_be_swapped_in()
    {
        var directory = NewDirectory();
        var first = (Token: NewToken(), Digest: new string('3', 64));
        var second = (Token: NewToken(), Digest: new string('4', 64));
        var stalePath = Path.Combine(directory, second.Token + ".result.json");
        WriteResult(stalePath, second.Token, second.Digest, true, "completed", new JsonObject { ["imagePath"] = "attacker.exe" });

        var swapped = Path.Combine(directory, first.Token + ".result.json");
        File.Copy(stalePath, swapped);

        await RejectsResult(swapped, first.Token, first.Digest);
        Assert.Equal(
            "attacker.exe",
            (await ReadResultAsync(swapped, second.Token, second.Digest, "nssm-manager.imagepath"))["imagePath"]!.GetValue<string>());
    }

    [Fact]
    public async Task Client_rejects_a_malformed_oversized_or_wrong_schema_result()
    {
        var directory = NewDirectory();
        var token = NewToken();
        var digest = new string('5', 64);

        var empty = Path.Combine(directory, "empty.result.json");
        await File.WriteAllTextAsync(empty, "");
        await RejectsResult(empty, token, digest);

        var oversized = Path.Combine(directory, "oversized.result.json");
        await File.WriteAllTextAsync(oversized, new string('x', (1024 * 1024) + 1));
        await RejectsResult(oversized, token, digest);

        var absent = Path.Combine(directory, "absent.result.json");
        await RejectsResult(absent, token, digest);

        var wrongSchema = Path.Combine(directory, "schema.result.json");
        await File.WriteAllTextAsync(wrongSchema, new JsonObject
        {
            ["schemaVersion"] = 2,
            ["token"] = token,
            ["requestDigest"] = digest,
            ["success"] = true,
            ["message"] = "completed",
            ["payload"] = new JsonObject()
        }.ToJsonString());
        await RejectsResult(wrongSchema, token, digest);
    }

    [Fact]
    public async Task A_failed_result_surfaces_the_remote_failure_instead_of_a_payload()
    {
        var directory = NewDirectory();
        var token = NewToken();
        var digest = new string('6', 64);
        var resultPath = Path.Combine(directory, token + ".result.json");
        WriteResult(
            resultPath,
            token,
            digest,
            false,
            "Service ImagePath changed after approval.",
            new JsonObject
            {
                ["exceptionType"] = typeof(InvalidOperationException).FullName,
                ["nativeErrorCode"] = 1060
            });

        var failure = await Assert.ThrowsAsync<NssmElevatedOperationException>(
            () => ReadResultAsync(resultPath, token, digest, "nssm-manager.apply"));

        Assert.Equal("nssm-manager.apply", failure.Operation);
        Assert.Equal("Service ImagePath changed after approval.", failure.Message);
        Assert.Equal(typeof(InvalidOperationException).FullName, failure.RemoteExceptionType);
        Assert.Equal(1060, failure.NativeErrorCode);
    }

    [Fact]
    public async Task The_client_refuses_to_stage_an_elevated_request_off_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => NssmElevatedClient.ExecuteAsync(
                "nssm-manager.rollback",
                new JsonObject { ["serviceName"] = "mpt-test" },
                CancellationToken.None));
    }

    private static JsonObject Envelope(
        string token,
        string? operation = null,
        string? moduleId = null,
        int schemaVersion = 1,
        string? brokerHash = null,
        JsonObject? arguments = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null)
    {
        var created = createdAt ?? DateTimeOffset.UtcNow;
        return new JsonObject
        {
            ["schemaVersion"] = schemaVersion,
            ["token"] = token,
            ["moduleId"] = moduleId ?? "nssm-manager",
            ["operation"] = operation ?? "nssm-manager.control",
            ["createdAt"] = created.ToString("O", CultureInfo.InvariantCulture),
            ["expiresAt"] = (expiresAt ?? created.AddMinutes(5)).ToString("O", CultureInfo.InvariantCulture),
            ["arguments"] = arguments ?? new JsonObject
            {
                ["serviceName"] = "mpt-nssm-security-test",
                ["action"] = "stop",
                ["expectedImagePath"] = @"C:\ProgramData\MyPowerTools\bin\nssm-manager\2.24.101\nssm-manager.exe"
            },
            ["broker"] = new JsonObject
            {
                ["path"] = BrokerProcessPath,
                ["sha256"] = brokerHash ?? BrokerProcessHash
            }
        };
    }

    private static void ValidateRoot(JsonObject root, string token, string? commandLineHash = null) =>
        Invoke("ValidateRoot", root, token, commandLineHash ?? BrokerProcessHash);

    private static void ValidateArguments(string operation, JsonObject arguments) =>
        Invoke("ValidateArguments", operation, arguments);

    private static void Rejected(JsonObject root, string token, string message, string? commandLineHash = null)
    {
        var rejected = Assert.Throws<InvalidDataException>(() => ValidateRoot(root, token, commandLineHash));
        Assert.Equal(message, rejected.Message);
    }

    private static void WriteResult(string path, string token, string digest, bool success, string message, JsonNode payload) =>
        Invoke("WriteResult", path, token, digest, success, message, payload);

    private static Task<int> ExecuteAsync(string[] commandLine, AuditLog audit) =>
        (Task<int>)Executor
            .GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [commandLine, audit])!;

    private static async Task<JsonNode> ReadResultAsync(string path, string token, string digest, string operation)
    {
        var method = typeof(NssmElevatedClient).GetMethod("ReadResultAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(NssmElevatedClient), "ReadResultAsync");
        try
        {
            return await (Task<JsonNode>)method.Invoke(null, [path, token, digest, operation, CancellationToken.None])!;
        }
        catch (TargetInvocationException exception)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
            throw;
        }
    }

    private static async Task RejectsResult(string path, string token, string digest) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadResultAsync(path, token, digest, "nssm-manager.control"));

    private static void Invoke(string name, params object?[] arguments)
    {
        var method = Executor.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(Executor.FullName, name);
        try
        {
            method.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
        }
    }

    private static StagedRequest StageRequest(JsonObject root, bool oversized)
    {
        var token = root["token"]!.GetValue<string>();
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools",
            "broker-requests",
            "nssm-manager",
            "mpt-nssm-security-test");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, token + ".json");
        var content = oversized
            ? new string('x', (1024 * 1024) + 1)
            : root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new StagedRequest(
            path,
            Path.Combine(directory, token + ".result.json"),
            NewAuditLog(),
            [
                "--request-file", path,
                "--token", token,
                "--digest", HashText(content),
                "--broker-sha256", BrokerProcessHash
            ]);
    }

    private static JsonObject ReadResultFile(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static string[] WithOption(string[] commandLine, string name, string value)
    {
        var copy = commandLine.ToArray();
        copy[Array.IndexOf(copy, name) + 1] = value;
        return copy;
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' was not found in the executor source.");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"'{end}' was not found after '{start}' in the executor source.");
        return source[from..to];
    }

    private static string NewToken() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-nssm-broker-security", NewToken());
        Directory.CreateDirectory(path);
        return path;
    }

    private static AuditLog NewAuditLog() =>
        new(Path.Combine(Path.GetTempPath(), "mpt-nssm-broker-audit", NewToken(), "audit.jsonl"));

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashText(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    /// <summary>
    /// The Elevated Broker ships as a self-contained WinExe, so the test project references it for
    /// build ordering only. Its build output is loaded from the shared artifacts tree instead.
    /// </summary>
    private static Assembly LoadElevatedBrokerAssembly()
    {
        var candidates = Directory.EnumerateFiles(
            Path.Combine(Root, "artifacts", "build", "bin", "MyPowerTools.ElevatedBroker"),
            "MyPowerTools.ElevatedBroker.dll",
            SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new FileNotFoundException("MyPowerTools.ElevatedBroker must be built before the NSSM security tests.");
        }
        return Assembly.LoadFrom(candidates[0]);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MyPowerTools.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record StagedRequest(string Path, string ResultPath, AuditLog Audit, string[] Arguments);
}
