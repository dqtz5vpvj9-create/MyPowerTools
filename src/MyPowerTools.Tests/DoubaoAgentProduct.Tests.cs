using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using DoubaoAgent.MyPowerTools;
using MyPowerTools.Abstractions;
using DoubaoAgent.Surface.Services;
using DoubaoAgent.Surface.ViewModels;

namespace MyPowerTools.Tests;

public sealed class DoubaoAgentProductTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public async Task Refresh_publishes_local_state_immediately_and_coalesces_slow_probes()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        var handler = new DoubaoRuntimeHandler { RequestDelay = TimeSpan.FromMilliseconds(500) };
        using var httpClient = new HttpClient(handler);
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            ConnectedTcpAsync,
            runtimeController: SafeRuntimeController());
        var updates = new List<DoubaoAgentSnapshot>();
        service.SnapshotChanged += snapshot =>
        {
            lock (updates)
            {
                updates.Add(snapshot);
            }
        };

        var stopwatch = Stopwatch.StartNew();
        var firstRefresh = service.RefreshAsync();
        var secondRefresh = service.RefreshAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));
        Assert.True(service.CurrentSnapshot.IsRefreshing);
        Assert.True(service.CurrentSnapshot.RuntimeInstalled);
        Assert.False(firstRefresh.IsCompleted);
        Assert.Same(firstRefresh, secondRefresh);

        var completed = await firstRefresh;

        Assert.False(completed.IsRefreshing);
        Assert.True(completed.AllServicesOnline);
        Assert.Equal(4, handler.Requests.Count);
        lock (updates)
        {
            Assert.Contains(updates, snapshot => snapshot.IsRefreshing && snapshot.RuntimeInstalled);
            Assert.Contains(updates, snapshot => !snapshot.IsRefreshing && snapshot.AllServicesOnline);
        }
    }

    [Fact]
    public void Doubao_navigation_mounts_cached_state_without_awaiting_business_probes()
    {
        var source = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Services",
            "ShellWorkspaceController.Tools.cs"));
        var start = source.IndexOf("private Task LoadDoubaoAgentToolAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task LoadSmartBirdThermostatToolAsync", start, StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("_doubaoAgentTools.CurrentSnapshot", method, StringComparison.Ordinal);
        Assert.Contains("SetOwnedContent", method, StringComparison.Ordinal);
        Assert.DoesNotContain("_doubaoAgentTools.LoadAsync", method, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_runtime_snapshot_uses_the_real_three_service_contract()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        using var httpClient = new HttpClient(new DoubaoRuntimeHandler());
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            ConnectedTcpAsync,
            runtimeController: SafeRuntimeController());

        var snapshot = await service.LoadAsync();

        Assert.True(snapshot.RuntimeInstalled);
        Assert.True(snapshot.SecretConfigured);
        Assert.True(snapshot.AllServicesOnline);
        Assert.Equal(3, snapshot.OnlineServiceCount);
        Assert.Contains(snapshot.Services, item => item.Id == "tool" && item.Endpoint.EndsWith(":38102", StringComparison.Ordinal));
        Assert.Contains(snapshot.Services, item => item.Id == "planner" && item.Endpoint.EndsWith(":38189", StringComparison.Ordinal));
        Assert.Contains(snapshot.Services, item => item.Id == "mcp" && item.Endpoint.Contains(":38080/sse", StringComparison.Ordinal));
        Assert.Equal(4, snapshot.Models.Count);
        Assert.True(snapshot.Overlay.IsRunning);
        Assert.True(snapshot.Configuration.ToolConfigExists);
        Assert.True(snapshot.Configuration.PlannerConfigExists);
        Assert.Equal(Environment.ProcessId, snapshot.Runtime.Processes.Single(item => item.Id == "planner").ProcessId);
        Assert.Equal(Environment.ProcessId, snapshot.Runtime.Processes.Single(item => item.Id == "tool").ProcessId);
        Assert.Equal(Environment.ProcessId, snapshot.Runtime.Processes.Single(item => item.Id == "mcp").ProcessId);
        Assert.Contains(snapshot.Logs, log => log.Name == "planner.out.log");
    }

    [Fact]
    public async Task Planner_stream_preserves_actions_screenshots_and_raw_json()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        var handler = new DoubaoRuntimeHandler();
        using var taskHttpClient = new HttpClient(handler);
        using var service = new DoubaoAgentToolService(
            taskHttpClient,
            runtime.Root,
            runtime.SecretFile,
            ConnectedTcpAsync,
            runtimeController: SafeRuntimeController());
        var events = new List<DoubaoAgentTaskEvent>();

        await service.RunTaskAsync(
            new DoubaoAgentTaskRequest("打开示例页面", "doubao-seed-2-0-lite-260428", DoubaoAgentToolService.DefaultSystemPrompt),
            item =>
            {
                events.Add(item);
                return Task.CompletedTask;
            });

        Assert.Equal(2, events.Count);
        Assert.Equal("agent_step", events[0].Kind);
        Assert.Equal("点击(120, 80)", events[0].Action);
        Assert.Equal($"打开页面{Environment.NewLine}解析动作: click", events[0].Detail);
        Assert.Contains("parsed_action", events[0].RawJson);
        Assert.StartsWith("data:image/png;base64,", events[1].ScreenshotDataUrl);
        Assert.Equal("已观察屏幕", events[1].Title);
        Assert.DoesNotContain("\"screenshot\":", events[1].RawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iVBORw0KGgo=", events[1].RawJson, StringComparison.Ordinal);
        var screenshotMetadata = JsonNode.Parse(events[1].RawJson)!["screenshot_metadata"]!.AsObject();
        Assert.Equal(12, screenshotMetadata["encodedCharacters"]!.GetValue<int>());
        Assert.Equal(8, screenshotMetadata["byteLength"]!.GetValue<int>());
        Assert.Equal(64, screenshotMetadata["sha256"]!.GetValue<string>().Length);
        Assert.Equal(1920, JsonNode.Parse(events[1].RawJson)!["screen"]!["width"]!.GetValue<int>());
        Assert.Equal(1080, JsonNode.Parse(events[1].RawJson)!["screen"]!["height"]!.GetValue<int>());
        var request = JsonNode.Parse(handler.LastTaskRequestJson)!.AsObject();
        Assert.Equal("打开示例页面", request["user_prompt"]!.GetValue<string>());
        Assert.Equal("doubao-seed-2-0-lite-260428", request["model_name"]!.GetValue<string>());
        Assert.Equal(DoubaoAgentToolService.DefaultSystemPrompt, request["system_prompt"]!.GetValue<string>());
    }

    [Fact]
    public async Task Planner_stream_exposes_retry_grounding_tool_results_and_errors()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        var handler = new DoubaoRuntimeHandler
        {
            TaskStream =
                "data: {\"event\":\"agent_step_retry\",\"parsed_summary\":\"寻找按钮\",\"warning\":\"retry\",\"conversion_error\":\"x out of range\",\"model_response\":\"raw model\",\"parsed_action\":\"click(2000,20)\"}\n\n" +
                "data: {\"event\":\"tool_result\",\"tool_name\":\"click\",\"tool_arguments\":{\"x\":120,\"y\":80},\"coordinate_transform\":{\"x\":120,\"y\":80},\"tool_result\":{\"ok\":true}}\n\n" +
                "data: {\"summary\":\"任务执行遇到问题\",\"error\":\"tool failed\"}\n\n"
        };
        using var httpClient = new HttpClient(handler);
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            ConnectedTcpAsync,
            runtimeController: SafeRuntimeController());
        var events = new List<DoubaoAgentTaskEvent>();

        await service.RunTaskAsync(
            new DoubaoAgentTaskRequest("执行测试", "doubao-seed-2-0-lite-260428", "system"),
            item =>
            {
                events.Add(item);
                return Task.CompletedTask;
            });

        Assert.Equal(3, events.Count);
        Assert.Equal("模型动作重试", events[0].Title);
        Assert.Contains("警告: retry", events[0].Detail);
        Assert.Contains("坐标转换错误: x out of range", events[0].Detail);
        Assert.Contains("模型响应: raw model", events[0].Detail);
        Assert.Equal("工具执行结果", events[1].Title);
        Assert.Contains("工具参数: {\"x\":120,\"y\":80}", events[1].Detail);
        Assert.Contains("坐标转换: {\"x\":120,\"y\":80}", events[1].Detail);
        Assert.Contains("工具结果: {\"ok\":true}", events[1].Detail);
        Assert.True(events[2].IsError);
        Assert.Equal("执行失败", events[2].Title);
        Assert.Contains("错误: tool failed", events[2].Detail);
    }

    [Fact]
    public async Task Planner_trace_text_is_bounded_after_screenshot_payloads_are_removed()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        var payload = new JsonObject
        {
            ["event"] = "message",
            ["message"] = new string('x', DoubaoAgentToolService.MaxTraceRawCharacters * 2)
        }.ToJsonString();
        var handler = new DoubaoRuntimeHandler { TaskStream = $"data: {payload}\n\n" };
        using var httpClient = new HttpClient(handler);
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            ConnectedTcpAsync,
            runtimeController: SafeRuntimeController());
        var events = new List<DoubaoAgentTaskEvent>();

        await service.RunTaskAsync(
            new DoubaoAgentTaskRequest("bounded", "doubao-seed-2-0-lite-260428", "system"),
            item =>
            {
                events.Add(item);
                return Task.CompletedTask;
            });

        var item = Assert.Single(events);
        Assert.True(item.Detail.Length <= DoubaoAgentToolService.MaxTraceDetailCharacters + 40);
        Assert.True(item.RawJson.Length < DoubaoAgentToolService.MaxTraceRawCharacters);
        Assert.Contains("\"truncated\": true", item.RawJson);
    }

    [Fact]
    public async Task Snapshot_merges_runtime_models_and_preserves_overlay_diagnostics()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        var handler = new DoubaoRuntimeHandler
        {
            ModelsJson = """
                {"models":[
                  {"name":"doubao-seed-2-0-lite-260428","display_name":"Runtime Seed 2.0"}
                ]}
                """
        };
        using var httpClient = new HttpClient(handler);
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            ConnectedTcpAsync,
            runtimeController: SafeRuntimeController());

        var snapshot = await service.LoadAsync();

        Assert.Equal(4, snapshot.Models.Count);
        Assert.Contains(snapshot.Models, model => model.Name == "doubao-seed-2-1-pro-260628");
        Assert.Contains(snapshot.Models, model => model.DisplayName == "Runtime Seed 2.0");
        Assert.Equal("exclude_from_capture", snapshot.Overlay.ExclusionMethod);
        Assert.True(snapshot.Overlay.IsReady);
        Assert.True(snapshot.Overlay.CaptureProtectionOk);
        Assert.Contains("affinity_mode", snapshot.Overlay.RawJson);
        Assert.Contains(snapshot.Services, serviceStatus =>
            serviceStatus.Id == "mcp" && serviceStatus.Detail.StartsWith("TCP 已连接", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tool_health_and_overlay_requests_use_the_private_auth_header_without_exposing_it()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        File.AppendAllText(runtime.SecretFile, $"{Environment.NewLine}AUTH_KEY=fixture-tool-private");
        var handler = new DoubaoRuntimeHandler();
        using var httpClient = new HttpClient(handler);
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            ConnectedTcpAsync,
            runtimeController: SafeRuntimeController());

        var snapshot = await service.LoadAsync();

        Assert.Equal("fixture-tool-private", handler.LastToolAuthKey);
        Assert.DoesNotContain("fixture-tool-private", snapshot.Overlay.RawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-tool-private", string.Join(" ", snapshot.Services.Select(item => item.Detail)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlay_workflow_maps_every_original_action_and_clamps_show_parameters()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        var handler = new DoubaoRuntimeHandler();
        using var httpClient = new HttpClient(handler);
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            ConnectedTcpAsync,
            runtimeController: SafeRuntimeController());

        Assert.True((await service.CallOverlayAsync("show", -20, -30, 100, 2)).Success);
        Assert.True((await service.CallOverlayAsync("hide", 1, 2, 300, 9)).Success);
        Assert.True((await service.CallOverlayAsync("self-test", 1, 2, 300, 9)).Success);
        Assert.True((await service.CallOverlayAsync("status", 1, 2, 300, 9)).Success);

        Assert.Contains(handler.Requests, request =>
            request.Contains("Action=ShowOverlay", StringComparison.Ordinal) &&
            request.Contains("PositionX=0", StringComparison.Ordinal) &&
            request.Contains("PositionY=0", StringComparison.Ordinal) &&
            request.Contains("DurationMs=200", StringComparison.Ordinal) &&
            request.Contains("Radius=8", StringComparison.Ordinal) &&
            request.Contains("Label=Doubao", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Contains("Action=HideOverlay", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Contains("Action=OverlaySelfTest", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Contains("Action=OverlayStatus", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Planner_task_cancellation_reaches_the_live_request()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        var handler = new DoubaoRuntimeHandler { BlockTaskUntilCanceled = true };
        using var httpClient = new HttpClient(handler);
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            ConnectedTcpAsync,
            runtimeController: SafeRuntimeController());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunTaskAsync(
            new DoubaoAgentTaskRequest("等待取消", "doubao-seed-2-0-lite-260428", "system"),
            _ => Task.CompletedTask,
            cancellation.Token));
    }

    [Fact]
    public async Task Auto_start_initialization_reuses_an_already_ready_runtime()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        var handler = new DoubaoRuntimeHandler();
        using var httpClient = new HttpClient(handler);
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            ConnectedTcpAsync,
            runtimeController: SafeRuntimeController());
        var offline = ReadySnapshot() with
        {
            Services = ReadySnapshot().Services
                .Select(item => item with { IsOnline = false, StatusCode = null, Detail = "未连接" })
                .ToArray()
        };
        using var viewModel = new DoubaoAgentViewModel(offline, service);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.AllServicesOnline);
        Assert.Equal("豆包 Computer Use 已在运行。", viewModel.ActionMessage);
        Assert.False(viewModel.HasActionError);
    }

    [Fact]
    public async Task Auto_start_launches_foundation_services_without_an_ark_key()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        File.WriteAllText(runtime.SecretFile, "");
        var handler = new DoubaoRuntimeHandler { IsAvailable = false };
        var controller = new FakeRuntimeController(SafeSecurity(owned: false));
        controller.OnStart = () =>
        {
            handler.IsAvailable = true;
            controller.State = SafeSecurity(owned: true);
        };
        using var httpClient = new HttpClient(handler);
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            (_, port, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(port == 38080 && handler.IsAvailable);
            },
            runtimeController: controller);
        var offline = await service.LoadAsync();
        using var viewModel = new DoubaoAgentViewModel(offline, service);

        Assert.False(viewModel.SecretConfigured);
        Assert.True(viewModel.AutoStartEnabled);
        Assert.True(viewModel.CanStartRuntime);

        await viewModel.InitializeAsync();

        Assert.Equal(1, controller.StartCount);
        Assert.True(viewModel.AllServicesOnline);
        Assert.Equal("服务在线，等待密钥", viewModel.RuntimeStatusText);
        Assert.False(viewModel.CanRunTask);
        Assert.True(viewModel.CanStopRuntime);
        Assert.True(viewModel.CanRestartRuntime);
    }

    [Fact]
    public async Task Runtime_control_uses_the_owned_controller_and_never_executes_legacy_scripts()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        var marker = Path.Combine(runtime.Root, "legacy-script-ran.marker");
        File.WriteAllText(Path.Combine(runtime.Root, "start-local-computer-use.ps1"), $"Set-Content -LiteralPath '{marker}' -Value start");
        File.WriteAllText(Path.Combine(runtime.Root, "stop-local-computer-use.ps1"), $"Set-Content -LiteralPath '{marker}' -Value stop");
        var handler = new DoubaoRuntimeHandler { IsAvailable = false };
        var controller = new FakeRuntimeController(SafeSecurity(owned: false));
        controller.OnStart = () =>
        {
            handler.IsAvailable = true;
            controller.State = SafeSecurity(owned: true);
        };
        controller.OnStop = () =>
        {
            handler.IsAvailable = false;
            controller.State = SafeSecurity(owned: false);
        };
        using var httpClient = new HttpClient(handler);
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            (_, port, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(port == 38080 && handler.IsAvailable);
            },
            shutdownReadyTimeout: TimeSpan.FromSeconds(1),
            runtimeController: controller);

        var started = await service.StartAsync();
        var stopped = await service.StopAsync();

        Assert.True(started.Success, started.TechnicalDetails);
        Assert.True(stopped.Success, stopped.TechnicalDetails);
        Assert.Equal(1, controller.StartCount);
        Assert.Equal(1, controller.StopCount);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Wildcard_listener_is_allowed_when_all_real_services_are_healthy()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        var controller = new FakeRuntimeController(new DoubaoRuntimeSecurityState(
            true,
            [new DoubaoTcpListenerState("0.0.0.0", 38189, 4040)],
            false,
            "wildcard listener"));
        var handler = new DoubaoRuntimeHandler();
        using var httpClient = new HttpClient(handler);
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            ConnectedTcpAsync,
            runtimeController: controller);

        service.Session.AutoStartEnabled = true;
        var snapshot = await service.LoadAsync();
        using var viewModel = new DoubaoAgentViewModel(snapshot, service);
        viewModel.Instruction = "打开设置";
        var overlay = await service.CallOverlayAsync("show", 1, 2, 300, 9);
        var events = new List<DoubaoAgentTaskEvent>();
        await service.RunTaskAsync(
            new DoubaoAgentTaskRequest("打开设置", "doubao-seed-2-0-lite-260428", "system"),
            taskEvent =>
            {
                events.Add(taskEvent);
                return Task.CompletedTask;
            });

        Assert.True(snapshot.AllServicesOnline);
        Assert.False(snapshot.RuntimeSecurity.HasUnsafeListeners);
        Assert.Empty(snapshot.Runtime.Processes);
        Assert.False(snapshot.HasOwnedProcesses);
        Assert.True(service.Session.AutoStartEnabled);
        Assert.Equal("可以执行任务", viewModel.RuntimeStatusText);
        Assert.True(viewModel.CanRunTask);
        Assert.True(viewModel.CanUseOverlay);
        Assert.False(viewModel.CanStartRuntime);
        Assert.False(viewModel.CanStopRuntime);
        Assert.True(overlay.Success, overlay.TechnicalDetails);
        Assert.NotEmpty(events);
    }

    [Fact]
    public async Task Restart_attempts_verified_legacy_stop_then_starts_managed_runtime()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        var unsafeOwned = new DoubaoRuntimeSecurityState(
            true,
            [new DoubaoTcpListenerState("0.0.0.0", 38189, 4040)],
            true,
            "verified legacy runtime",
            [new DoubaoOwnedProcessState("legacy", 4040, 38189, DateTimeOffset.UtcNow, true, "fixture")]);
        var controller = new FakeRuntimeController(unsafeOwned);
        var handler = new DoubaoRuntimeHandler { IsAvailable = true };
        controller.OnStop = () =>
        {
            handler.IsAvailable = false;
            controller.State = SafeSecurity(owned: false);
        };
        controller.OnStart = () =>
        {
            handler.IsAvailable = true;
            controller.State = SafeSecurity(owned: true);
        };
        using var httpClient = new HttpClient(handler);
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            (_, port, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(port == 38080 && handler.IsAvailable);
            },
            shutdownReadyTimeout: TimeSpan.FromSeconds(1),
            runtimeController: controller);

        var restarted = await service.RestartAsync();

        Assert.True(restarted.Success, restarted.TechnicalDetails);
        Assert.Equal(1, controller.StopCount);
        Assert.Equal(1, controller.StartCount);
        Assert.True(controller.State.IsSafe);
        Assert.True(controller.State.HasOwnedProcesses);
    }

    [Fact]
    public async Task Overlay_http_200_requires_running_ready_visible_and_ok_semantics()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        var handler = new DoubaoRuntimeHandler
        {
            OverlayActionJson = "{\"Result\":{\"ok\":true,\"running\":false,\"ready\":false,\"visible\":false}}"
        };
        using var httpClient = new HttpClient(handler);
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            ConnectedTcpAsync,
            runtimeController: SafeRuntimeController());

        var result = await service.CallOverlayAsync("show", 100, 100, 500, 12);

        Assert.False(result.Success);
        Assert.Contains("running=false", result.TechnicalDetails);
    }

    [Fact]
    public void Secure_controller_uses_fixed_loopback_commands_strict_identity_and_bounded_cleanup()
    {
        var controller = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "doubao-computer-use",
            "current-integration",
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Services",
            "DoubaoSecureRuntimeController.cs"));
        var service = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "doubao-computer-use",
            "current-integration",
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Services",
            "DoubaoAgentToolService.cs"));

        Assert.Contains("GetExtendedTcpTable", controller);
        Assert.Contains("NtQueryInformationProcess", controller);
        var commandLineQueryStart = controller.IndexOf("private static Task<string> QueryCommandLineAsync", StringComparison.Ordinal);
        var commandLineQueryEnd = controller.IndexOf("private static Task<ProcessSnapshot?> QueryProcessSnapshotAsync", commandLineQueryStart, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Get-CimInstance Win32_Process",
            controller[commandLineQueryStart..commandLineQueryEnd],
            StringComparison.Ordinal);
        Assert.Contains("CommandLineToArgvW", controller);
        Assert.Contains("SequenceEqual(identity.CommandArguments", controller);
        Assert.Contains("--host\", \"127.0.0.1\", \"--port\", \"38102", controller);
        Assert.Contains("FASTMCP_HOST\", \"127.0.0.1", controller);
        Assert.Contains("--host\", \"127.0.0.1\", \"--port\", \"38189", controller);
        Assert.Contains("SelectEnvironment(secrets, \"AUTH_KEY\")", controller);
        Assert.Contains("SelectEnvironment(secrets, \"AUTH_API_KEY\")", controller);
        Assert.Contains("SelectEnvironment(secrets, \"ARK_API_KEY\")", controller);
        Assert.Contains("startInfo.Environment.Remove(name)", controller);
        Assert.Contains("catch (OperationCanceledException)", controller);
        Assert.Contains("StopStartedProcessesAfterFailureAsync(started)", controller);
        Assert.Contains("TimeSpan.FromSeconds(8)", controller);
        Assert.Contains("local-computer-use.pids.json", controller);
        Assert.Contains("IsDescendantOfAsync", controller);
        Assert.Contains("CommandArgumentsEqual", controller);
        Assert.Contains("旧豆包运行时身份验证失败，所有进程均保持运行。", controller);
        Assert.DoesNotContain("start-local-computer-use.ps1", controller);
        Assert.DoesNotContain("stop-local-computer-use.ps1", controller);
        Assert.DoesNotContain("start-local-computer-use.ps1", service);
        Assert.DoesNotContain("stop-local-computer-use.ps1", service);
    }

    [Fact]
    public async Task Owned_stop_rejects_substring_similar_arguments_and_accepts_exact_identity_without_listener()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var runtime = new TemporaryDoubaoRuntime();
        var powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var exactArguments = new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"
        };
        var startInfo = new ProcessStartInfo
        {
            FileName = powerShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in exactArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        try
        {
            WriteOwnedState(runtime.Root, process, powerShell,
                ["-No", "-NoPro", "-Non", "-Com", "Start-Sleep"]);
            using var controller = new DoubaoSecureRuntimeController();

            var rejected = await controller.StopAsync(runtime.Root);

            Assert.False(rejected.Success);
            Assert.False(process.HasExited);

            WriteOwnedState(runtime.Root, process, powerShell, exactArguments);
            var inspection = await controller.InspectAsync(runtime.Root);
            if (!inspection.HasOwnedProcesses)
            {
                throw new InvalidOperationException(
                    "Native identity inspection failed: " + inspection.Detail + " | " +
                    string.Join(" | ", inspection.OwnedProcesses?.Select(item => item.Detail) ?? []));
            }
            var stopped = await controller.StopAsync(runtime.Root);

            Assert.True(stopped.Success, stopped.TechnicalDetails);
            await process.WaitForExitAsync();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Windows_tcp_owner_inspection_reads_target_listeners_without_mutation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var runtime = new TemporaryDoubaoRuntime();
        using var controller = new DoubaoSecureRuntimeController();

        var security = await controller.InspectAsync(runtime.Root);

        Assert.True(security.InspectionAvailable, security.Detail);
        Assert.All(security.Listeners, listener =>
        {
            Assert.Contains(listener.Port, DoubaoSecureRuntimeController.ServicePorts);
            Assert.True(IPAddress.TryParse(listener.Address, out _));
            Assert.True(listener.ProcessId > 0);
        });
    }

    [Fact]
    public void Product_view_model_covers_the_original_wpf_workflow_and_runtime_details()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        using var httpClient = new HttpClient(new DoubaoRuntimeHandler());
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            ConnectedTcpAsync,
            runtimeController: SafeRuntimeController());
        var snapshot = ReadySnapshot();
        using var viewModel = new DoubaoAgentViewModel(snapshot, service);

        Assert.Equal("豆包 Computer Use", viewModel.Title);
        Assert.True(viewModel.AllServicesOnline);
        Assert.True(viewModel.CanRunTask);
        Assert.True(viewModel.AutoStartEnabled);
        Assert.Equal(4, viewModel.Models.Count);
        Assert.Equal("doubao-seed-2-0-lite-260428", viewModel.SelectedModel?.Name);
        Assert.Equal(3, viewModel.RuntimeProcesses.Count);
        Assert.True(viewModel.HasLogs);
        Assert.Contains("支付", DoubaoAgentToolService.DefaultSystemPrompt);
        Assert.Contains("调用用户", DoubaoAgentToolService.DefaultSystemPrompt);
        Assert.Contains("PID 101", viewModel.DiagnosticText);
        Assert.Contains("38189", viewModel.DiagnosticText);
        Assert.NotNull(viewModel.ShowOverlayCommand);
        Assert.NotNull(viewModel.OverlaySelfTestCommand);
        Assert.NotNull(viewModel.StopRuntimeCommand);
        Assert.NotNull(viewModel.RestartRuntimeCommand);
        Assert.Equal(64, DoubaoAgentViewModel.MaxTraceItems);
    }

    [Fact]
    public void Product_session_keeps_model_prompt_auto_start_and_overlay_values_across_navigation()
    {
        using var runtime = new TemporaryDoubaoRuntime();
        using var httpClient = new HttpClient(new DoubaoRuntimeHandler());
        using var service = new DoubaoAgentToolService(
            httpClient,
            runtime.Root,
            runtime.SecretFile,
            ConnectedTcpAsync,
            runtimeController: SafeRuntimeController());
        using (var first = new DoubaoAgentViewModel(ReadySnapshot(), service))
        {
            first.AutoStartEnabled = false;
            first.SelectedModel = first.Models.Single(model => model.Name == "doubao-seed-2-1-pro-260628");
            first.Instruction = "保留本次输入";
            first.SystemPrompt = "保留高级系统提示";
            first.ShowSystemPrompt = true;
            first.OverlayX = 321;
            first.OverlayY = 654;
            first.OverlayDurationMs = 2400;
            first.OverlayRadius = 45;
        }

        using var restored = new DoubaoAgentViewModel(ReadySnapshot(), service);

        Assert.False(restored.AutoStartEnabled);
        Assert.Equal("doubao-seed-2-1-pro-260628", restored.SelectedModel?.Name);
        Assert.Equal("保留本次输入", restored.Instruction);
        Assert.Equal("保留高级系统提示", restored.SystemPrompt);
        Assert.True(restored.ShowSystemPrompt);
        Assert.Equal(321, restored.OverlayX);
        Assert.Equal(654, restored.OverlayY);
        Assert.Equal(2400, restored.OverlayDurationMs);
        Assert.Equal(45, restored.OverlayRadius);
    }

    [Fact]
    public void Dedicated_view_keeps_every_original_panel_and_a_single_real_route()
    {
        var view = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "doubao-computer-use",
            "current-integration",
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Views",
            "DoubaoAgentView.axaml"));
        var tool = JsonNode.Parse(File.ReadAllText(Path.Combine(
            Root,
            "modules",
            "doubao-agent",
            "ui",
            "tool.json")))!.AsObject();
        var routes = tool["routes"]!.AsArray();

        Assert.Contains("运行任务", view);
        Assert.Contains("本地服务", view);
        Assert.Contains("截图预览", view);
        Assert.Contains("运行记录", view);
        Assert.Contains("记录详情", view);
        Assert.Contains("原始数据", view);
        Assert.Contains("运行时诊断", view);
        Assert.Contains("屏幕标记调试", view);
        Assert.Contains("进程、端口与日志", view);
        Assert.Contains("OverlaySelfTestCommand", view);
        Assert.Contains("MaxWidth=\"1480\"", view);
        Assert.Single(routes);
        Assert.Equal("services", routes[0]!["routeId"]!.GetValue<string>());
    }

    [Fact]
    public void Module_and_static_commands_map_the_real_service_roles()
    {
        var module = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "doubao-computer-use",
            "current-integration",
            "src",
            "DoubaoAgent.MyPowerTools",
            "DoubaoAgentModule.cs"));
        var commands = File.ReadAllText(Path.Combine(
            Root,
            "modules",
            "doubao-agent",
            "commands.index.json"));

        Assert.Contains("Agent Planner\", \"http://127.0.0.1:38189\", \"/health", module);
        Assert.Contains("Tool Server\", \"http://127.0.0.1:38102\", \"/config", module);
        Assert.Contains("MCP Server\", \"http://127.0.0.1:38080\", \"/sse", module);
        Assert.Contains("Planner on port 38189", commands);
        Assert.Contains("Tool Server on port 38102", commands);
        Assert.Contains("MCP SSE endpoint on port 38080", commands);
        Assert.DoesNotContain("planner service on port 38102", commands, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plannerBaseUrl", commands, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_settings_fix_all_internal_endpoints_and_force_canonical_values()
    {
        var module = new DoubaoAgentModule();
        try
        {
            var schema = JsonNode.Parse((await module.GetSettingsSchemaAsync(CancellationToken.None)).SchemaJson)!.AsObject();
            var properties = schema["properties"]!.AsObject();
            foreach (var (name, expected) in new Dictionary<string, string>
                     {
                         ["plannerBaseUrl"] = "http://127.0.0.1:38189",
                         ["toolBaseUrl"] = "http://127.0.0.1:38102",
                         ["mcpBaseUrl"] = "http://127.0.0.1:38080",
                         ["plannerHealthPath"] = "/health",
                         ["toolHealthPath"] = "/config",
                         ["mcpHealthPath"] = "/sse"
                     })
            {
                var property = properties[name]!.AsObject();
                Assert.Equal(expected, property["const"]!.GetValue<string>());
                Assert.True(property["readOnly"]!.GetValue<bool>());
            }

            var rejected = await module.ValidateSettingsAsync(
                new SettingsPatch(module.Id, 1, new JsonObject
                {
                    ["plannerBaseUrl"] = "http://localhost:38189",
                    ["toolHealthPath"] = "/other"
                }),
                CancellationToken.None);
            Assert.False(rejected.Ok);

            var applied = await module.ApplySettingsAsync(
                new SettingsSnapshotDocument(module.Id, 7, new JsonObject
                {
                    ["plannerBaseUrl"] = "http://127.0.0.1:9999",
                    ["toolBaseUrl"] = "http://127.0.0.1:9998",
                    ["mcpBaseUrl"] = "http://127.0.0.1:9997",
                    ["plannerHealthPath"] = "/wrong",
                    ["toolHealthPath"] = "/wrong",
                    ["mcpHealthPath"] = "/wrong",
                    ["redactSensitiveOutput"] = false
                }, DateTimeOffset.UtcNow),
                CancellationToken.None);
            Assert.Equal("http://127.0.0.1:38189", applied.Values["plannerBaseUrl"]!.GetValue<string>());
            Assert.Equal("http://127.0.0.1:38102", applied.Values["toolBaseUrl"]!.GetValue<string>());
            Assert.Equal("http://127.0.0.1:38080", applied.Values["mcpBaseUrl"]!.GetValue<string>());
            Assert.Equal("/health", applied.Values["plannerHealthPath"]!.GetValue<string>());
            Assert.Equal("/config", applied.Values["toolHealthPath"]!.GetValue<string>());
            Assert.Equal("/sse", applied.Values["mcpHealthPath"]!.GetValue<string>());
            Assert.False(applied.Values["redactSensitiveOutput"]!.GetValue<bool>());
        }
        finally
        {
            await module.DisposeAsync(CancellationToken.None);
        }
    }

    private static DoubaoAgentSnapshot ReadySnapshot()
    {
        return new DoubaoAgentSnapshot(
            @"C:\fixture\doubao-computer-use-local",
            true,
            true,
            [
                new DoubaoAgentServiceStatus("planner", "Agent Planner", "http://127.0.0.1:38189", true, "HTTP 200 · 8 ms", 200, 8),
                new DoubaoAgentServiceStatus("tool", "Tool Server", "http://127.0.0.1:38102", true, "HTTP 200 · 5 ms", 200, 5),
                new DoubaoAgentServiceStatus("mcp", "MCP Server", "http://127.0.0.1:38080/sse", true, "HTTP 200 · 3 ms", 200, 3)
            ],
            [
                new DoubaoAgentModel("doubao-1.5-ui-tars-250328", "Doubao-1.5-UI-TARS"),
                new DoubaoAgentModel("doubao-seed-2-0-lite-260428", "Doubao-Seed-2.0-Lite"),
                new DoubaoAgentModel("doubao-seed-2-1-turbo-260628", "Doubao-Seed-2.1-Turbo"),
                new DoubaoAgentModel("doubao-seed-2-1-pro-260628", "Doubao-Seed-2.1-Pro")
            ],
            new DoubaoAgentOverlayStatus(
                true,
                false,
                "exclude_from_capture",
                "{\"running\":true,\"ready\":true}",
                true,
                true),
            [new DoubaoAgentLogFile("planner.out.log", 4096, DateTimeOffset.Now)],
            new DoubaoAgentRuntimeState(
                DateTimeOffset.Now.AddMinutes(-5),
                true,
                [
                    new DoubaoAgentRuntimeProcess("planner", "Agent Planner", 38189, 101, true, true),
                    new DoubaoAgentRuntimeProcess("tool", "Tool Server", 38102, 102, true, true),
                    new DoubaoAgentRuntimeProcess("mcp", "MCP Server", 38080, 103, true, true)
                ]),
            new DoubaoAgentConfigurationState(true, true, true, true, @"C:\fixture\secrets\doubao-computer-use.env"),
            DateTimeOffset.Now,
            SafeSecurity(owned: true));
    }

    private static FakeRuntimeController SafeRuntimeController(bool owned = true) =>
        new(SafeSecurity(owned));

    private static DoubaoRuntimeSecurityState SafeSecurity(bool owned)
    {
        var ownedProcesses = owned
            ? new[]
            {
                new DoubaoOwnedProcessState("planner", Environment.ProcessId, 38189, DateTimeOffset.UtcNow.AddMinutes(-1), true, "fixture"),
                new DoubaoOwnedProcessState("tool", Environment.ProcessId, 38102, DateTimeOffset.UtcNow.AddMinutes(-1), true, "fixture"),
                new DoubaoOwnedProcessState("mcp", Environment.ProcessId, 38080, DateTimeOffset.UtcNow.AddMinutes(-1), true, "fixture")
            }
            : [];
        return new DoubaoRuntimeSecurityState(
            true,
            owned
                ? [
                    new DoubaoTcpListenerState("127.0.0.1", 38189, Environment.ProcessId),
                    new DoubaoTcpListenerState("127.0.0.1", 38102, Environment.ProcessId),
                    new DoubaoTcpListenerState("127.0.0.1", 38080, Environment.ProcessId)
                ]
                : [],
            owned,
            "fixture safe",
            ownedProcesses);
    }

    private static Task<bool> ConnectedTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            port == 38080 &&
            (string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)));
    }

    private static void WriteOwnedState(
        string runtimeRoot,
        Process process,
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        var argumentNodes = new JsonArray();
        foreach (var argument in arguments)
        {
            argumentNodes.Add(argument);
        }
        var state = new JsonObject
        {
            ["Version"] = 1,
            ["CreatedAtUtc"] = DateTimeOffset.UtcNow,
            ["Processes"] = new JsonArray
            {
                new JsonObject
                {
                    ["Id"] = "fixture",
                    ["ProcessId"] = process.Id,
                    ["ExecutablePath"] = Path.GetFullPath(executablePath),
                    ["StartedAtUtc"] = process.StartTime.ToUniversalTime(),
                    ["Port"] = 38102,
                    ["CommandArguments"] = argumentNodes
                }
            }
        };
        var logsDirectory = DoubaoSecureRuntimeController.ResolveLogsDirectory(runtimeRoot);
        Directory.CreateDirectory(logsDirectory);
        var path = Path.Combine(logsDirectory, "mypowertools-secure-runtime.json");
        File.WriteAllText(path, state.ToJsonString());
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

    private sealed class FakeRuntimeController : IDoubaoSecureRuntimeController
    {
        public FakeRuntimeController(DoubaoRuntimeSecurityState state)
        {
            State = state;
        }

        public DoubaoRuntimeSecurityState State { get; set; }
        public Action? OnStart { get; set; }
        public Action? OnStop { get; set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public Task<DoubaoRuntimeSecurityState> InspectAsync(
            string runtimeRoot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(State);
        }

        public Task<DoubaoAgentOperationResult> StartAsync(
            string runtimeRoot,
            string secretFilePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            if (!State.IsSafe)
            {
                return Task.FromResult(new DoubaoAgentOperationResult(false, "unsafe", State.Detail));
            }
            OnStart?.Invoke();
            return Task.FromResult(new DoubaoAgentOperationResult(true, "started", "fixture"));
        }

        public Task<DoubaoAgentOperationResult> StopAsync(
            string runtimeRoot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            if (!State.HasOwnedProcesses)
            {
                return Task.FromResult(new DoubaoAgentOperationResult(false, "not-owned", "fixture"));
            }
            OnStop?.Invoke();
            return Task.FromResult(new DoubaoAgentOperationResult(true, "stopped", "fixture"));
        }

        public void Dispose()
        {
        }
    }

    private sealed class DoubaoRuntimeHandler : HttpMessageHandler
    {
        public string LastTaskRequestJson { get; private set; } = "";
        public string LastToolAuthKey { get; private set; } = "";
        public List<string> Requests { get; } = [];
        public bool BlockTaskUntilCanceled { get; init; }
        public bool IsAvailable { get; set; } = true;
        public TimeSpan RequestDelay { get; init; }
        public string OverlayActionJson { get; init; } = "";
        public string AvailabilityMarkerPath { get; init; } = "";
        public string UnavailableMarkerPath { get; init; } = "";
        public string TaskStream { get; init; } =
            "data: {\"event\":\"agent_step\",\"action\":\"点击(120, 80)\",\"summary\":\"打开页面\",\"parsed_action\":\"click\"}\n\n" +
            "data: {\"event\":\"screenshot\",\"screen\":{\"width\":1920,\"height\":1080},\"screenshot\":\"iVBORw0KGgo=\"}\n\n" +
            "data: [DONE]\n\n";
        public string ModelsJson { get; init; } = """
            {"models":[
              {"name":"doubao-1.5-ui-tars-250328","display_name":"Doubao-1.5-UI-TARS"},
              {"name":"doubao-seed-2-0-lite-260428","display_name":"Doubao-Seed-2.0-Lite"},
              {"name":"doubao-seed-2-1-turbo-260628","display_name":"Doubao-Seed-2.1-Turbo"},
              {"name":"doubao-seed-2-1-pro-260628","display_name":"Doubao-Seed-2.1-Pro"}
            ]}
            """;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.PathAndQuery);
            if (RequestDelay > TimeSpan.Zero)
            {
                await Task.Delay(RequestDelay, cancellationToken);
            }
            if (uri.Port == 38102 && request.Headers.TryGetValues("X-API-Key", out var authValues))
            {
                LastToolAuthKey = authValues.Single();
            }
            if (!IsAvailable ||
                (!string.IsNullOrWhiteSpace(AvailabilityMarkerPath) &&
                 !File.Exists(AvailabilityMarkerPath)) ||
                (!string.IsNullOrWhiteSpace(UnavailableMarkerPath) &&
                 File.Exists(UnavailableMarkerPath)))
            {
                return Response("{\"error\":\"offline\"}", statusCode: HttpStatusCode.ServiceUnavailable);
            }
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/run/task")
            {
                LastTaskRequestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (BlockTaskUntilCanceled)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                return Response(TaskStream, "text/event-stream");
            }

            if (uri.AbsolutePath == "/models")
            {
                return Response(ModelsJson);
            }
            if (uri.Query.Contains("OverlayStatus", StringComparison.Ordinal))
            {
                return Response("{\"Result\":{\"running\":true,\"ready\":true,\"visible\":false,\"affinity_ok\":true,\"affinity_mode\":\"exclude_from_capture\"}}");
            }
            if (uri.Query.Contains("Action=", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(OverlayActionJson))
                {
                    return Response(OverlayActionJson);
                }
                if (uri.Query.Contains("ShowOverlay", StringComparison.Ordinal))
                {
                    return Response("{\"Result\":{\"ok\":true,\"running\":true,\"ready\":true,\"visible\":true}}");
                }
                if (uri.Query.Contains("HideOverlay", StringComparison.Ordinal))
                {
                    return Response("{\"Result\":{\"ok\":true,\"running\":true,\"ready\":true,\"visible\":false}}");
                }
                return Response("{\"Result\":{\"ok\":true,\"running\":true,\"ready\":true,\"visible\":false}}");
            }
            if (uri.AbsolutePath == "/health")
            {
                return Response("{\"status\":\"ok\"}");
            }
            if (uri.AbsolutePath == "/config")
            {
                return Response("{}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Response(
            string body,
            string mediaType = "application/json",
            HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            };
        }
    }

    private sealed class TemporaryDoubaoRuntime : IDisposable
    {
        private const string DataRootEnvironmentVariable = "DOUBAO_COMPUTER_USE_DATA_ROOT";
        private readonly string? _previousDataRoot;

        public TemporaryDoubaoRuntime()
        {
            _previousDataRoot = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
            Root = Path.Combine(Path.GetTempPath(), "mypowertools-doubao-tests", Guid.NewGuid().ToString("N"));
            DataRoot = Path.Combine(Root, "data");
            Environment.SetEnvironmentVariable(DataRootEnvironmentVariable, DataRoot);
            SecretFile = Path.Combine(Root, "secrets", "doubao-computer-use.env");
            Directory.CreateDirectory(Path.Combine(Root, "tool_server"));
            Directory.CreateDirectory(Path.Combine(Root, "mcp_server"));
            Directory.CreateDirectory(Path.Combine(Root, "planner"));
            Directory.CreateDirectory(Path.Combine(Root, "tool_server", ".venv", "Scripts"));
            Directory.CreateDirectory(Path.Combine(Root, "mcp_server", ".venv", "Scripts"));
            Directory.CreateDirectory(Path.Combine(Root, "mcp_server", "src", "mcp_server"));
            Directory.CreateDirectory(Path.Combine(Root, "planner", ".venv", "Scripts"));
            Directory.CreateDirectory(Path.Combine(Root, "planner", "src", "planner"));
            Directory.CreateDirectory(Path.Combine(DataRoot, "logs"));
            Directory.CreateDirectory(Path.GetDirectoryName(SecretFile)!);
            File.WriteAllText(Path.Combine(Root, "start-local-computer-use.ps1"), "# fixture");
            File.WriteAllText(Path.Combine(Root, "stop-local-computer-use.ps1"), "# fixture");
            File.WriteAllText(Path.Combine(Root, "tool_server", ".venv", "Scripts", "python.exe"), "fixture");
            File.WriteAllText(Path.Combine(Root, "tool_server", "main.py"), "# fixture");
            File.WriteAllText(Path.Combine(Root, "mcp_server", ".venv", "Scripts", "python.exe"), "fixture");
            File.WriteAllText(Path.Combine(Root, "mcp_server", "src", "mcp_server", "main.py"), "# fixture");
            File.WriteAllText(Path.Combine(Root, "planner", ".venv", "Scripts", "python.exe"), "fixture");
            File.WriteAllText(Path.Combine(Root, "planner", "src", "planner", "app.py"), "# fixture");
            File.WriteAllText(Path.Combine(Root, "tool_server", "config.toml"), "port = 38102");
            File.WriteAllText(Path.Combine(Root, "planner", "config.toml"), "# fixture");
            File.WriteAllText(SecretFile, "ARK_API_KEY=fixture-secret");
            File.WriteAllText(Path.Combine(DataRoot, "logs", "planner.out.log"), "ready");
            File.WriteAllText(Path.Combine(DataRoot, "logs", "local-computer-use.pids.json"), $$"""
            {
              "started_at": "{{DateTimeOffset.Now:O}}",
              "tool_port": 38102,
              "mcp_port": 38080,
              "planner_port": 38189,
              "tool_server_pid": {{Environment.ProcessId}},
              "mcp_server_pid": {{Environment.ProcessId}},
              "planner_pid": {{Environment.ProcessId}}
            }
            """);
        }

        public string Root { get; }
        public string DataRoot { get; }
        public string SecretFile { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(DataRootEnvironmentVariable, _previousDataRoot);
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
