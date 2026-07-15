using AdbForwarder.Surface.Services;

namespace MyPowerTools.Tests;

public sealed class AdbForwardingWorkflowTests
{
    private const string Serial = "USB123";
    private const string WirelessSerial = "10.0.0.8:5555";

    [Fact]
    public async Task Wireless_workflow_preserves_network_configuration_and_forwards_selected_endpoint()
    {
        var runner = new FakeAdbForwarderProcessRunner();
        var service = CreateService(runner);
        var request = new AdbForwardRequest(
            WirelessSerial,
            ConnectionMode: AdbForwardConnectionMode.Wireless);

        var preflight = await service.PreflightAsync(request, brokerAvailable: true);
        var result = await service.RunForwardAsync(request, null, null, null);

        Assert.True(preflight.CanRun, preflight.Summary);
        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(runner.Requests, call =>
            call.Arguments.Contains("getprop") ||
            call.Arguments.Contains("setprop") ||
            call.Arguments.Contains("tcpip"));
        Assert.Contains(runner.Requests, call => Matches(
            call,
            "adb",
            "-s", WirelessSerial, "-a", "forward", "tcp:15556", "tcp:5555"));
        Assert.Contains(result.Steps, step =>
            step.Id == "device-tcp" &&
            step.State == AdbForwarderStepState.Skipped &&
            step.Detail.Contains("不修改", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("USB123", AdbForwardConnectionMode.Wireless)]
    [InlineData("10.0.0.8:5555", AdbForwardConnectionMode.Wired)]
    public async Task Connection_mode_rejects_the_wrong_device_transport(
        string serial,
        AdbForwardConnectionMode mode)
    {
        var runner = new FakeAdbForwarderProcessRunner();
        var service = CreateService(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => service.PreflightAsync(
            new AdbForwardRequest(serial, ConnectionMode: mode),
            brokerAvailable: true));

        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Local_workflow_preserves_source_command_order_and_skips_all_ssh_calls()
    {
        var runner = new FakeAdbForwarderProcessRunner { DeviceTcpPort = "4444" };
        var service = CreateService(runner);

        var result = await service.RunForwardAsync(
            new AdbForwardRequest(Serial),
            null,
            null,
            null,
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var requests = runner.Requests;
        var setProp = FindRequest(requests, "adb", "-s", Serial, "shell", "setprop", "persist.adb.tcp.port", "5555");
        var verifiedProp = FindRequestAfter(requests, setProp, "adb", "-s", Serial, "shell", "getprop", "persist.adb.tcp.port");
        var tcpip = FindRequestAfter(requests, verifiedProp, "adb", "-s", Serial, "tcpip", "5555");
        var wait = FindRequestAfter(requests, tcpip, "adb", "-s", Serial, "wait-for-device");
        var forward = FindRequestAfter(requests, wait, "adb", "-s", Serial, "-a", "forward", "tcp:15556", "tcp:5555");

        Assert.True(setProp < verifiedProp);
        Assert.True(verifiedProp < tcpip);
        Assert.True(tcpip < wait);
        Assert.True(wait < forward);
        Assert.Empty(runner.LongRunningRequests);
        Assert.DoesNotContain(requests, request =>
            request.FileName.Contains("ssh", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Steps, step => step.Id == "remote-aosp" && step.State == AdbForwarderStepState.Skipped);
    }

    [Fact]
    public async Task Offline_endpoint_is_disconnected_and_connect_attempts_are_bounded()
    {
        var runner = new FakeAdbForwarderProcessRunner { AutoConnect = false };
        runner.EndpointStates["127.0.0.1:15556"] = "offline";
        var service = CreateService(runner);

        var result = await service.RunForwardAsync(
            new AdbForwardRequest(Serial),
            null,
            null,
            null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("4", result.Message, StringComparison.Ordinal);
        Assert.Single(runner.Requests.Where(request => Matches(request, "adb", "disconnect", "127.0.0.1:15556")));
        Assert.Equal(4, runner.Requests.Count(request => Matches(request, "adb", "connect", "127.0.0.1:15556")));
        Assert.DoesNotContain(runner.Requests, request => Matches(request, "adb", "connect", "127.0.0.1:15557"));
    }

    [Fact]
    public async Task Ssh_workflow_owns_one_long_running_tunnel_and_sends_fixed_remote_script_over_stdin()
    {
        var runner = new FakeAdbForwarderProcessRunner();
        var service = CreateService(runner);

        var result = await service.RunForwardAsync(
            new AdbForwardRequest(Serial, IncludeSsh: true),
            null,
            null,
            null,
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var tunnel = Assert.Single(runner.LongRunningRequests);
        Assert.Equal("ssh-forward.exe", tunnel.FileName);
        Assert.Contains("-CNg", tunnel.Arguments);
        Assert.DoesNotContain("-CfNg", tunnel.Arguments);
        Assert.Contains("ExitOnForwardFailure=yes", tunnel.Arguments);
        Assert.Contains("15557:127.0.0.1:15557", tunnel.Arguments);
        Assert.Equal([4242], result.CleanupState.TunnelProcessIds);

        var remote = Assert.Single(runner.Requests.Where(request =>
            request.FileName == "ssh" && request.Arguments.Contains("bash")));
        Assert.NotNull(remote.StandardInput);
        Assert.DoesNotContain('\r', remote.StandardInput!);
        Assert.Contains("set -euo pipefail", remote.StandardInput!, StringComparison.Ordinal);
        Assert.Contains("kill-server", remote.StandardInput!, StringComparison.Ordinal);
        Assert.Contains("STATE=", remote.StandardInput!, StringComparison.Ordinal);
        Assert.Contains("remount", remote.StandardInput!, StringComparison.Ordinal);
        Assert.Contains("/android/aosp/out/soong/host/linux-x86/bin/adb", remote.Arguments);
    }

    [Fact]
    public async Task Broker_approval_pauses_before_any_mutating_device_command()
    {
        var runner = new FakeAdbForwarderProcessRunner { HasPortProxy = false, DeviceTcpPort = "4444" };
        var service = CreateService(runner);

        var result = await service.RunForwardAsync(
            new AdbForwardRequest(Serial),
            null,
            (_, _, _) => Task.FromResult(new AdbForwarderBrokerRequestResult(
                AdbForwarderBrokerDisposition.ApprovalRequired,
                "approval opened")),
            null,
            CancellationToken.None);

        Assert.True(result.ApprovalRequired);
        Assert.True(result.CleanupState.PortProxyRequested);
        Assert.DoesNotContain(runner.Requests, IsMutatingRequest);
        Assert.Empty(runner.LongRunningRequests);
    }

    [Fact]
    public async Task Cleanup_stops_only_the_owned_tunnel_process_id()
    {
        var runner = new FakeAdbForwarderProcessRunner();
        var service = CreateService(runner);
        var ownership = runner.OwnedTunnel;
        var cleanup = new AdbForwardCleanupState(false, "", false, false, false, true, [4242], [], [ownership]);

        var result = await service.CleanupAsync(
            new AdbForwardRequest(Serial),
            cleanup,
            null,
            null,
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal([4242], Assert.Single(runner.StopCalls));
        Assert.Empty(result.CleanupState.TunnelProcessIds);
    }

    [Fact]
    public async Task Cleanup_preserves_process_when_persisted_identity_does_not_match()
    {
        var runner = new FakeAdbForwarderProcessRunner { ReportMismatchedIdentity = true };
        var service = CreateService(runner);
        var cleanup = new AdbForwardCleanupState(
            false,
            "",
            false,
            false,
            false,
            true,
            [4242],
            [],
            [runner.OwnedTunnel]);

        var result = await service.CleanupAsync(
            new AdbForwardRequest(Serial),
            cleanup,
            null,
            null,
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Empty(runner.StopCalls);
        Assert.Contains(result.Steps, step => step.Id == "cleanup-tunnel" && step.State == AdbForwarderStepState.Skipped);
    }

    [Fact]
    public async Task Cleanup_retains_verified_owned_process_when_tree_does_not_exit()
    {
        var runner = new FakeAdbForwarderProcessRunner();
        runner.StopFailureIds.Add(runner.OwnedTunnel.ProcessId);
        var service = CreateService(runner);
        var cleanup = new AdbForwardCleanupState(
            false,
            "",
            false,
            false,
            false,
            true,
            [runner.OwnedTunnel.ProcessId],
            [],
            [runner.OwnedTunnel]);

        var result = await service.CleanupAsync(
            new AdbForwardRequest(Serial),
            cleanup,
            null,
            null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.CleanupState.TunnelStarted);
        Assert.Equal([runner.OwnedTunnel.ProcessId], result.CleanupState.TunnelProcessIds);
        Assert.Equal([runner.OwnedTunnel], result.CleanupState.TunnelOwnership);
        Assert.Contains("保留", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Persisted_state_protects_usb_serial_and_round_trips_for_current_user()
    {
        var statePath = Path.Combine(Path.GetTempPath(), "mpt-adb-workflow", Guid.NewGuid().ToString("N"), "state.json");
        var store = new AdbForwarderWorkflowStateStore(statePath);
        var state = new AdbForwarderPersistedWorkflowState(
            new AdbForwardRequest(Serial, IncludeSsh: true),
            new AdbForwardCleanupState(false, "", false, false, false, true, [4242], [], []),
            true,
            DateTimeOffset.UtcNow);

        store.Save(state);

        var persistedText = File.ReadAllText(statePath);
        Assert.DoesNotContain(Serial, persistedText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(OperatingSystem.IsWindows() ? "dpapi-current-user" : "user-bound-aes-gcm", persistedText, StringComparison.Ordinal);
        var loaded = Assert.IsType<AdbForwarderPersistedWorkflowState>(store.Load());
        Assert.Equal(Serial, loaded.Request.DeviceSerial);
        Assert.True(loaded.ApprovalRequired);
    }

    [Fact]
    public async Task External_command_failures_and_workflow_events_redact_usb_serial()
    {
        var runner = new FakeAdbForwarderProcessRunner { FailWaitForDeviceWithSerial = true };
        var service = CreateService(runner);
        var events = new List<AdbForwarderWorkflowEvent>();

        var result = await service.RunForwardAsync(
            new AdbForwardRequest(Serial),
            null,
            null,
            workflowEvent =>
            {
                events.Add(workflowEvent);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain(Serial, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Steps, step => step.Detail.Contains(Serial, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(events, workflowEvent =>
            workflowEvent.Message.Contains(Serial, StringComparison.OrdinalIgnoreCase) ||
            workflowEvent.Step.Detail.Contains(Serial, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("<adb-device-", result.Message, StringComparison.Ordinal);

        runner.FailWaitForDeviceWithSerial = false;
        runner.FailAdbVersionWithSerial = true;
        var preflight = await service.PreflightAsync(new AdbForwardRequest(Serial), false, CancellationToken.None);
        Assert.DoesNotContain(preflight.Checks, check => check.Detail.Contains(Serial, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Production_workflow_resolves_system_tools_and_defaults_tunnel_to_windows_ssh()
    {
        var service = new AdbForwardingWorkflowService(isWindows: true);

        Assert.Equal("adb", ReadPrivatePath(service, "_adbPath"));
        Assert.True(Path.IsPathFullyQualified(ReadPrivatePath(service, "_netshPath")));
        Assert.True(Path.IsPathFullyQualified(ReadPrivatePath(service, "_wherePath")));
        Assert.True(Path.IsPathFullyQualified(ReadPrivatePath(service, "_sshPath")));
        Assert.True(Path.IsPathFullyQualified(ReadPrivatePath(service, "_sshForwardPath")));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                Path.Combine(Environment.SystemDirectory, "netsh.exe"),
                ReadPrivatePath(service, "_netshPath"),
                ignoreCase: true);
            Assert.Equal(
                Path.Combine(Environment.SystemDirectory, "where.exe"),
                ReadPrivatePath(service, "_wherePath"),
                ignoreCase: true);
            Assert.Equal(
                Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe"),
                ReadPrivatePath(service, "_sshPath"),
                ignoreCase: true);
            Assert.Equal(
                Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe"),
                ReadPrivatePath(service, "_sshForwardPath"),
                ignoreCase: true);
        }
    }

    [Fact]
    public void Production_workflow_uses_only_an_existing_absolute_tunnel_wrapper()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mpt-adb-workflow", Guid.NewGuid().ToString("N"));
        var wrapperPath = Path.Combine(directory, "trusted-ssh-wrapper.exe");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(wrapperPath, [0x4D, 0x5A]);
        try
        {
            var withWrapper = new AdbForwardingWorkflowService(
                isWindows: true,
                sshForwardPath: wrapperPath);
            Assert.Equal(
                Path.GetFullPath(wrapperPath),
                ReadPrivatePath(withWrapper, "_sshForwardPath"),
                ignoreCase: true);

            var missingWrapper = new AdbForwardingWorkflowService(
                isWindows: true,
                sshForwardPath: Path.Combine(directory, "missing.exe"));
            Assert.Equal(
                ReadPrivatePath(missingWrapper, "_sshPath"),
                ReadPrivatePath(missingWrapper, "_sshForwardPath"),
                ignoreCase: true);

            var relativeWrapper = new AdbForwardingWorkflowService(
                isWindows: true,
                sshForwardPath: Path.GetFileName(wrapperPath));
            Assert.Equal(
                ReadPrivatePath(relativeWrapper, "_sshPath"),
                ReadPrivatePath(relativeWrapper, "_sshForwardPath"),
                ignoreCase: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Persisted_approval_is_adopted_only_after_the_exact_rule_is_verified()
    {
        var runner = new FakeAdbForwarderProcessRunner { HasPortProxy = false };
        var statePath = Path.Combine(Path.GetTempPath(), "mpt-adb-workflow", Guid.NewGuid().ToString("N"), "state.json");
        var store = new AdbForwarderWorkflowStateStore(statePath);
        var service = CreateService(runner, store);
        var request = new AdbForwardRequest(Serial);

        var pending = await service.RunForwardAsync(
            request,
            null,
            (_, _, _) => Task.FromResult(new AdbForwarderBrokerRequestResult(
                AdbForwarderBrokerDisposition.ApprovalRequired,
                "approval staged")),
            null,
            CancellationToken.None);
        Assert.True(pending.CleanupState.PortProxyRequested);

        var resumed = await service.RunForwardAsync(
            request,
            pending.CleanupState,
            (_, _, _) =>
            {
                runner.HasPortProxy = true;
                return Task.FromResult(new AdbForwarderBrokerRequestResult(
                    AdbForwarderBrokerDisposition.Applied,
                    "approval executed",
                    Changed: true));
            },
            null,
            CancellationToken.None);

        Assert.True(resumed.Success, resumed.Message);
        Assert.True(resumed.CleanupState.PortProxyOwned);
        Assert.False(resumed.CleanupState.PortProxyRequested);
    }

    [Fact]
    public async Task Exact_rule_created_externally_during_approval_is_not_adopted_as_owned()
    {
        var runner = new FakeAdbForwarderProcessRunner { HasPortProxy = false };
        var service = CreateService(runner);
        var result = await service.RunForwardAsync(
            new AdbForwardRequest(Serial),
            null,
            (_, _, _) =>
            {
                runner.HasPortProxy = true;
                return Task.FromResult(new AdbForwarderBrokerRequestResult(
                    AdbForwarderBrokerDisposition.Applied,
                    "target already satisfied externally",
                    Changed: false));
            },
            null,
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.False(result.CleanupState.PortProxyOwned);
        Assert.False(result.CleanupState.PortProxyRequested);
    }

    [Fact]
    public async Task Tunnel_ownership_survives_service_recreation_and_only_verified_process_is_stopped()
    {
        var runner = new FakeAdbForwarderProcessRunner();
        var statePath = Path.Combine(Path.GetTempPath(), "mpt-adb-workflow", Guid.NewGuid().ToString("N"), "state.json");
        var store = new AdbForwarderWorkflowStateStore(statePath);
        var first = CreateService(runner, store);
        var request = new AdbForwardRequest(Serial, IncludeSsh: true);
        var established = await first.RunForwardAsync(request, null, null, null, CancellationToken.None);
        Assert.True(established.Success, established.Message);

        var second = CreateService(runner, new AdbForwarderWorkflowStateStore(statePath));
        var repeated = await second.RunForwardAsync(request, null, null, null, CancellationToken.None);

        Assert.True(repeated.Success, repeated.Message);
        Assert.Contains(runner.StopCalls, call => call.SequenceEqual([4242]));
    }

    [Fact]
    public async Task Cancellation_during_connect_backoff_returns_canceled_after_one_attempt()
    {
        var runner = new FakeAdbForwarderProcessRunner { AutoConnect = false };
        using var cancellation = new CancellationTokenSource();
        var service = new AdbForwardingWorkflowService(
            runner,
            (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            isWindows: true,
            adbPath: "adb",
            sshPath: "ssh",
            sshForwardPath: "ssh-forward.exe");

        var result = await service.RunForwardAsync(
            new AdbForwardRequest(Serial),
            null,
            null,
            null,
            cancellation.Token);

        Assert.True(result.Canceled);
        Assert.Single(runner.Requests.Where(request => Matches(request, "adb", "connect", "127.0.0.1:15556")));
    }

    [Fact]
    public async Task Real_process_runner_timeout_kills_entire_process_tree_without_opening_a_window()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new AdbForwarderProcessRunner();
        var pwshPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell",
            "7",
            "pwsh.exe");
        Assert.True(File.Exists(pwshPath), $"PowerShell 7 executable missing: {pwshPath}");
        var lockPath = Path.Combine(Path.GetTempPath(), $"mpt-adb-timeout-{Guid.NewGuid():N}.lock");
        var escapedLockPath = lockPath.Replace("'", "''", StringComparison.Ordinal);
        var escapedPwshPath = pwshPath.Replace("'", "''", StringComparison.Ordinal);
        var childScript = $"$stream=[IO.File]::Open('{escapedLockPath}','OpenOrCreate','ReadWrite','None'); Start-Sleep -Seconds 30";
        var childEncoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(childScript));
        var parentScript = $$"""
            $psi = [Diagnostics.ProcessStartInfo]::new()
            $psi.FileName = '{{escapedPwshPath}}'
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true
            $psi.ArgumentList.Add('-NoLogo')
            $psi.ArgumentList.Add('-NoProfile')
            $psi.ArgumentList.Add('-NonInteractive')
            $psi.ArgumentList.Add('-EncodedCommand')
            $psi.ArgumentList.Add('{{childEncoded}}')
            $child = [Diagnostics.Process]::Start($psi)
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            while (-not (Test-Path -LiteralPath '{{escapedLockPath}}')) {
                if ([DateTime]::UtcNow -ge $deadline) { throw 'child did not acquire lock' }
                Start-Sleep -Milliseconds 20
            }
            Start-Sleep -Seconds 30
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(parentScript));
        var result = await runner.RunAsync(new AdbForwarderProcessRequest(
            pwshPath,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
            TimeSpan.FromSeconds(2)));

        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.True(result.Duration < TimeSpan.FromSeconds(5));
        FileStream? exclusive = null;
        for (var attempt = 0; attempt < 30 && exclusive is null; attempt++)
        {
            try
            {
                exclusive = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
        }
        Assert.NotNull(exclusive);
        await exclusive.DisposeAsync();
    }

    [Theory]
    [InlineData("-evil", "/android/adb")]
    [InlineData("r743", "/android/adb;rm")]
    [InlineData("r743", "/android/$(touch-pwned)")]
    public async Task Remote_host_and_path_reject_shell_metacharacters(string host, string path)
    {
        var runner = new FakeAdbForwarderProcessRunner();
        var service = CreateService(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => service.PreflightAsync(
            new AdbForwardRequest(Serial, IncludeSsh: true, RemoteHost: host, RemoteAdbPath: path),
            false,
            CancellationToken.None));

        Assert.Empty(runner.Requests);
    }

    private static AdbForwardingWorkflowService CreateService(
        FakeAdbForwarderProcessRunner runner,
        IAdbForwarderWorkflowStateStore? stateStore = null)
    {
        return new AdbForwardingWorkflowService(
            runner,
            (_, _) => Task.CompletedTask,
            isWindows: true,
            adbPath: "adb",
            sshPath: "ssh",
            sshForwardPath: "ssh-forward.exe",
            stateStore: stateStore);
    }

    private static string ReadPrivatePath(AdbForwardingWorkflowService service, string fieldName)
    {
        var field = typeof(AdbForwardingWorkflowService).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<string>(field?.GetValue(service));
    }

    private static int FindRequest(
        IReadOnlyList<AdbForwarderProcessRequest> requests,
        string fileName,
        params string[] arguments)
    {
        var index = requests.ToList().FindIndex(request => Matches(request, fileName, arguments));
        Assert.True(index >= 0, $"Missing request: {fileName} {string.Join(' ', arguments)}");
        return index;
    }

    private static int FindRequestAfter(
        IReadOnlyList<AdbForwarderProcessRequest> requests,
        int after,
        string fileName,
        params string[] arguments)
    {
        for (var index = after + 1; index < requests.Count; index++)
        {
            if (Matches(requests[index], fileName, arguments))
            {
                return index;
            }
        }
        Assert.Fail($"Missing request after {after}: {fileName} {string.Join(' ', arguments)}");
        return -1;
    }

    private static bool Matches(AdbForwarderProcessRequest request, string fileName, params string[] arguments)
    {
        return string.Equals(request.FileName, fileName, StringComparison.OrdinalIgnoreCase) &&
               request.Arguments.SequenceEqual(arguments);
    }

    private static bool IsMutatingRequest(AdbForwarderProcessRequest request)
    {
        if (!string.Equals(request.FileName, "adb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return request.Arguments.Contains("setprop") ||
               request.Arguments.Contains("tcpip") ||
               request.Arguments.Contains("-a") ||
               request.Arguments.FirstOrDefault() is "connect" or "disconnect";
    }

    private sealed class FakeAdbForwarderProcessRunner : IAdbForwarderProcessRunner
    {
        public List<AdbForwarderProcessRequest> Requests { get; } = [];
        public List<AdbForwarderLongRunningProcessRequest> LongRunningRequests { get; } = [];
        public List<IReadOnlyList<int>> StopCalls { get; } = [];
        public Dictionary<string, string> EndpointStates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string DeviceTcpPort { get; set; } = "5555";
        public bool HasForward { get; set; }
        public bool HasPortProxy { get; set; } = true;
        public bool AutoConnect { get; set; } = true;
        public bool ReportMismatchedIdentity { get; set; }
        public bool FailWaitForDeviceWithSerial { get; set; }
        public bool FailAdbVersionWithSerial { get; set; }
        public HashSet<int> StopFailureIds { get; } = [];
        public AdbForwarderOwnedProcess OwnedTunnel { get; } = new(4242, 638877888000000000, "C:\\Tools\\ssh-forward.exe", "owned-token");

        public Task<AdbForwarderProcessResult> RunAsync(
            AdbForwarderProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.FileName == "netsh" && request.Arguments.SequenceEqual(["interface", "portproxy", "show", "v4tov4"]))
            {
                return Success(HasPortProxy ? "0.0.0.0 15557 127.0.0.1 15556" : "");
            }
            if (request.FileName == "where.exe" && request.Arguments.SequenceEqual(["ssh-forward.exe"]))
            {
                return Success("C:\\Tools\\ssh-forward.exe");
            }
            if (request.FileName == "ssh")
            {
                return Success("");
            }
            if (request.FileName != "adb")
            {
                return Failed($"Unexpected executable: {request.FileName}");
            }
            if (request.Arguments.SequenceEqual(["version"]))
            {
                if (FailAdbVersionWithSerial)
                {
                    return Failed($"adb failed while inspecting {Serial}");
                }
                return Success("Android Debug Bridge version 1.0.41");
            }
            if (request.Arguments.SequenceEqual(["devices", "-l"]))
            {
                return Success(DeviceList());
            }
            if (request.Arguments.SequenceEqual(["disconnect", "127.0.0.1:15556"]) ||
                request.Arguments.SequenceEqual(["disconnect", "127.0.0.1:15557"]))
            {
                EndpointStates.Remove(request.Arguments[1]);
                return Success($"disconnected {request.Arguments[1]}");
            }
            if (request.Arguments.SequenceEqual(["connect", "127.0.0.1:15556"]) ||
                request.Arguments.SequenceEqual(["connect", "127.0.0.1:15557"]))
            {
                if (AutoConnect)
                {
                    EndpointStates[request.Arguments[1]] = "device";
                }
                return Success($"connected to {request.Arguments[1]}");
            }
            if (request.Arguments.SequenceEqual(["-s", Serial, "wait-for-device"]))
            {
                if (FailWaitForDeviceWithSerial)
                {
                    return Failed($"device {Serial} did not become ready");
                }
                return Success("");
            }
            if (request.Arguments.SequenceEqual(["-s", WirelessSerial, "wait-for-device"]))
            {
                return Success("");
            }
            if (request.Arguments.SequenceEqual(["-s", Serial, "shell", "getprop", "persist.adb.tcp.port"]))
            {
                return Success(DeviceTcpPort);
            }
            if (request.Arguments.SequenceEqual(["-s", Serial, "shell", "setprop", "persist.adb.tcp.port", "5555"]))
            {
                DeviceTcpPort = "5555";
                return Success("");
            }
            if (request.Arguments.SequenceEqual(["-s", Serial, "tcpip", "5555"]))
            {
                return Success("restarting in TCP mode port: 5555");
            }
            if (request.Arguments.SequenceEqual(["-s", Serial, "forward", "--list"]))
            {
                return Success(HasForward ? $"{Serial} tcp:15556 tcp:5555" : "");
            }
            if (request.Arguments.SequenceEqual(["-s", WirelessSerial, "forward", "--list"]))
            {
                return Success(HasForward ? $"{WirelessSerial} tcp:15556 tcp:5555" : "");
            }
            if (request.Arguments.SequenceEqual(["-s", Serial, "-a", "forward", "tcp:15556", "tcp:5555"]))
            {
                HasForward = true;
                return Success("");
            }
            if (request.Arguments.SequenceEqual(["-s", WirelessSerial, "-a", "forward", "tcp:15556", "tcp:5555"]))
            {
                HasForward = true;
                return Success("");
            }
            if (request.Arguments.SequenceEqual(["-s", Serial, "forward", "--remove", "tcp:15556"]))
            {
                HasForward = false;
                return Success("");
            }
            if (request.Arguments.SequenceEqual(["-s", Serial, "usb"]))
            {
                DeviceTcpPort = "";
                return Success("");
            }

            return Failed($"Unexpected adb arguments: {string.Join(' ', request.Arguments)}");
        }

        public Task<bool> CanConnectAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public Task<IReadOnlyList<int>> FindProcessIdsAsync(
            string processName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<int>>([]);
        }

        public Task<AdbForwarderProcessStopResult> StopProcessesAsync(
            IReadOnlyList<int> processIds,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls.Add(processIds.ToArray());
            var remaining = processIds.Where(StopFailureIds.Contains).ToArray();
            var stopped = processIds.Except(remaining).ToArray();
            return Task.FromResult(new AdbForwarderProcessStopResult(stopped, remaining));
        }

        public Task<AdbForwarderLongRunningProcessResult> StartLongRunningAsync(
            AdbForwarderLongRunningProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LongRunningRequests.Add(request);
            return Task.FromResult(new AdbForwarderLongRunningProcessResult(true, 4242, "", OwnedTunnel));
        }

        public Task<AdbForwarderOwnedProcess?> GetProcessIdentityAsync(
            int processId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processId != OwnedTunnel.ProcessId)
            {
                return Task.FromResult<AdbForwarderOwnedProcess?>(null);
            }
            return Task.FromResult<AdbForwarderOwnedProcess?>(ReportMismatchedIdentity
                ? OwnedTunnel with { StartTimeUtcTicks = OwnedTunnel.StartTimeUtcTicks + 1 }
                : OwnedTunnel with { OwnershipToken = "" });
        }

        private string DeviceList()
        {
            var lines = new List<string>
            {
                "List of devices attached",
                $"{Serial}\tdevice product:husky model:Pixel_8 transport_id:1",
                $"{WirelessSerial}\tdevice product:husky model:Pixel_8_WiFi transport_id:2"
            };
            lines.AddRange(EndpointStates.Select(pair => $"{pair.Key}\t{pair.Value}"));
            return string.Join('\n', lines);
        }

        private static Task<AdbForwarderProcessResult> Success(string output)
        {
            return Task.FromResult(new AdbForwarderProcessResult(true, 0, output, "", false, TimeSpan.Zero));
        }

        private static Task<AdbForwarderProcessResult> Failed(string error)
        {
            return Task.FromResult(new AdbForwarderProcessResult(true, 1, "", error, false, TimeSpan.Zero));
        }
    }
}
