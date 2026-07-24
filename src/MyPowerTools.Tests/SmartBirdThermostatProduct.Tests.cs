using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Protocol;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.WebSurface.Avalonia;
using SmartBird.Surface.Services;
using SmartBird.Surface.ViewModels;
using SmartBird.Surface.Views;
using SmartBirdThermostat.MyPowerTools;
using MptCommandRequest = MyPowerTools.Abstractions.CommandRequest;

namespace MyPowerTools.Tests;

public sealed class SmartBirdThermostatProductTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public async Task Service_uses_module_base_url_and_parses_the_real_status_shape()
    {
        Uri? requestedUri = null;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestedUri = request.RequestUri;
            return JsonResponse("""
                {
                  "enabled": true,
                  "mode": "dewpoint_protection",
                  "last_key": 0,
                  "last_decision": {
                    "surface_c": 29.317066,
                    "dew_point_c": 27.123174,
                    "off_threshold_c": 32.123174,
                    "on_threshold_c": 36.123174
                  },
                  "switch": {
                    "desired_key": 0,
                    "reported_key": 0,
                    "client_count": 1
                  },
                  "service": { "uptime_sec": 110551 }
                }
                """);
        }));
        using var service = new SmartBirdThermostatToolService(httpClient);

        var snapshot = await service.LoadAsync();

        Assert.Equal(new Uri("http://127.0.0.1:19002/api/status"), requestedUri);
        Assert.Equal(new Uri("http://127.0.0.1:19002/"), snapshot.DashboardUri);
        Assert.True(snapshot.IsOnline);
        Assert.Equal("dewpoint_protection", snapshot.Mode);
        Assert.False(snapshot.CoolingEnabled);
        Assert.Equal(1, snapshot.ClientCount);
        Assert.Equal(29.317066, snapshot.SurfaceC);
        Assert.Contains("表面 29.3 °C", snapshot.StatusDetail);
        Assert.Contains("1 台 SmartBird 设备在线", snapshot.StatusDetail);
    }

    [Fact]
    public async Task Unavailable_service_returns_an_actionable_offline_snapshot()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                ReasonPhrase = "Service Unavailable"
            }));
        using var service = new SmartBirdThermostatToolService(httpClient);

        var snapshot = await service.LoadAsync();

        Assert.False(snapshot.IsOnline);
        Assert.Equal("ServiceUnavailable", snapshot.ErrorCode);
        Assert.Contains("503", snapshot.StatusDetail);
    }

    [Fact]
    public async Task Status_probe_rejects_a_response_redirected_to_another_loopback_service()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            var response = JsonResponse("""
                {
                  "mode": "dewpoint_protection",
                  "switch": { "client_count": 0 }
                }
                """);
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "http://127.0.0.1:18988/api/status");
            return response;
        }));
        using var service = new SmartBirdThermostatToolService(httpClient);

        var snapshot = await service.LoadAsync();

        Assert.False(snapshot.IsOnline);
        Assert.Equal("cross-origin-response", snapshot.ErrorCode);
        Assert.Equal(SmartBirdThermostatToolService.DefaultBaseUri, snapshot.DashboardUri);
    }

    [Fact]
    public void Product_service_canonicalizes_every_candidate_to_the_source_task_origin()
    {
        Assert.Equal(
            SmartBirdThermostatToolService.DefaultBaseUri,
            SmartBirdThermostatToolService.NormalizeBaseUri(
                new Uri("http://localhost:19002/alternate")));
        Assert.True(SmartBirdThermostatToolService.IsDashboardOrigin(
            new Uri("http://127.0.0.1:19002/api/status")));
        Assert.False(SmartBirdThermostatToolService.IsDashboardOrigin(
            new Uri("http://127.0.0.1:18988/api/status")));
    }

    [Fact]
    public void Web_surface_policy_normalizes_the_dashboard_origin()
    {
        var dashboard = new Uri("http://127.0.0.1:19002/");

        Assert.True(WebSurfaceNavigationPolicy.IsSupportedWebUri(dashboard));
        Assert.Equal(dashboard, WebSurfaceNavigationPolicy.NormalizeOrigin(
            new Uri("http://127.0.0.1:19002/ui?range=24h")));
        Assert.True(WebSurfaceNavigationPolicy.IsSupportedWebUri(
            new Uri("http://localhost:19002/")));
        Assert.True(WebSurfaceNavigationPolicy.IsSupportedWebUri(
            new Uri("https://127.0.0.1:19002/")));
        Assert.True(WebSurfaceNavigationPolicy.IsSupportedWebUri(
            new Uri("file:///C:/Windows/System32/drivers/etc/hosts")));
        Assert.False(WebSurfaceNavigationPolicy.IsSupportedWebUri(
            new Uri("ftp://example.com/panel")));
        Assert.False(WebSurfaceNavigationPolicy.IsSupportedWebUri(
            new Uri("https://user:secret@example.com/panel")));
    }

    [Fact]
    public void Headless_view_model_keeps_a_full_workspace_fallback()
    {
        var snapshot = new SmartBirdThermostatSnapshot(
            new Uri("http://127.0.0.1:19002/"),
            IsOnline: true,
            Mode: "dewpoint_protection",
            CoolingEnabled: false,
            ClientCount: 1,
            SurfaceC: 29.3,
            StatusTitle: "SmartBird 服务已连接",
            StatusDetail: "表面 29.3 °C · 制冷已关闭 · 1 台设备在线",
            CheckedAt: DateTimeOffset.Now,
            ErrorCode: "");
        var viewModel = new SmartBirdThermostatViewModel(
            snapshot,
            embeddedBrowserSupported: false);

        Assert.True(viewModel.IsServiceOnline);
        Assert.False(viewModel.IsWebViewHostVisible);
        Assert.True(viewModel.IsFallbackVisible);
        Assert.True(viewModel.IsBrowserUnavailable);
        Assert.Equal("当前环境无法嵌入 WebView2", viewModel.FallbackTitle);
        Assert.Equal("露点保护", viewModel.ModeLabel);
        Assert.Equal("制冷关闭", viewModel.CoolingLabel);
    }

    [Fact]
    public void Browser_failure_falls_back_without_losing_the_live_service_state()
    {
        var snapshot = new SmartBirdThermostatSnapshot(
            new Uri("http://127.0.0.1:19002/"),
            IsOnline: true,
            Mode: "experiment_unconditional",
            CoolingEnabled: true,
            ClientCount: 1,
            SurfaceC: 36.2,
            StatusTitle: "SmartBird 服务已连接",
            StatusDetail: "在线",
            CheckedAt: DateTimeOffset.Now,
            ErrorCode: "");
        var viewModel = new SmartBirdThermostatViewModel(snapshot);

        Assert.True(viewModel.IsWebViewHostVisible);
        viewModel.SetEmbeddedBrowserFailed("navigation failed");

        Assert.True(viewModel.IsServiceOnline);
        Assert.False(viewModel.IsWebViewHostVisible);
        Assert.True(viewModel.IsFallbackVisible);
        Assert.Equal("SmartBird 控制台加载失败", viewModel.FallbackTitle);
        Assert.Equal("navigation failed", viewModel.FallbackDetail);
    }

    [Fact]
    public async Task Browser_fallback_opens_the_same_dashboard_uri()
    {
        Uri? opened = null;
        var snapshot = SmartBirdThermostatSnapshot.Offline(
            new Uri("http://127.0.0.1:19002/"),
            "离线",
            "请启动服务");
        var viewModel = new SmartBirdThermostatViewModel(
            snapshot,
            openExternal: uri =>
            {
                opened = uri;
                return Task.CompletedTask;
            },
            embeddedBrowserSupported: false);

        await viewModel.OpenExternalAsync(viewModel.DashboardUri);

        Assert.Equal(new Uri("http://127.0.0.1:19002/"), opened);
    }

    [Fact]
    public async Task Browser_fallback_refuses_every_other_loopback_origin()
    {
        var snapshot = SmartBirdThermostatSnapshot.Offline(
            SmartBirdThermostatToolService.DefaultBaseUri,
            "离线",
            "请启动服务");
        var viewModel = new SmartBirdThermostatViewModel(
            snapshot,
            openExternal: _ => Task.CompletedTask,
            embeddedBrowserSupported: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            viewModel.OpenExternalAsync(new Uri("http://127.0.0.1:18988/")));

        Assert.Contains("127.0.0.1:19002", error.Message);
    }

    [Fact]
    public void Tool_manifest_exposes_one_real_embedded_console_route()
    {
        var manifestPath = Path.Combine(
            Root,
            "modules",
            "smartbird-thermostat",
            "ui",
            "tool.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var routes = manifest["routes"]!.AsArray();
        var route = Assert.Single(routes)!.AsObject();

        Assert.Equal("overview", manifest["primaryRouteId"]!.GetValue<string>());
        Assert.Equal("overview", route["routeId"]!.GetValue<string>());
        Assert.Equal("smartbird-thermostat.overview", route["surfaceId"]!.GetValue<string>());
        Assert.Equal("打开温度管理器", manifest["homeCard"]!["primaryActionLabel"]!.GetValue<string>());
    }

    [Fact]
    public void Product_view_uses_the_sdk_web_surface_host_capability()
    {
        var surfaceRoot = Path.Combine(
            Root,
            "tools",
            "smartbird-thermostat",
            "current-integration",
            "src",
            "SmartBird.Surface");
        var view = File.ReadAllText(Path.Combine(surfaceRoot, "Views", "SmartBirdThermostatView.axaml"));
        var viewCode = File.ReadAllText(Path.Combine(surfaceRoot, "Views", "SmartBirdThermostatView.axaml.cs"));
        var sessionController = File.ReadAllText(Path.Combine(surfaceRoot, "Views", "SmartBirdWebSurfaceSessionController.cs"));
        var factory = File.ReadAllText(Path.Combine(surfaceRoot, "SmartBirdSurfaceFactory.cs"));
        var webSurfaceClient = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.WebSurface.Avalonia",
            "AvaloniaWebSurfaceService.cs"));
        var hostRoot = Path.Combine(Root, "src", "MyPowerTools.WebToolHost");
        var hostProject = File.ReadAllText(Path.Combine(hostRoot, "MyPowerTools.WebToolHost.csproj"));
        var hostWindow = File.ReadAllText(Path.Combine(hostRoot, "WebSurfaceHostWindow.cs"));
        var hostProgram = File.ReadAllText(Path.Combine(hostRoot, "Program.cs"));
        var shellChrome = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Views",
            "ShellChromeView.axaml.cs"));

        Assert.Contains("EmbeddedBrowserHost", view);
        Assert.DoesNotContain("SmartBirdWebView", view);
        Assert.Contains("在浏览器中打开", view);
        Assert.Contains("Focusable=\"True\"", view);
        Assert.Contains("AutomationProperties.Name=\"SmartBird 温度管理器网页控制台\"", view);
        Assert.Contains("context.WebSurfaces", factory);
        Assert.Contains("IMptWebSurfaceService", viewCode);
        Assert.Contains("CreateSession(new MptWebSurfaceRequest", sessionController);
        Assert.Contains("MyPowerTools.WebToolHost.exe", webSurfaceClient);
        Assert.Contains("CreateNoWindow = true", webSurfaceClient);
        Assert.Contains("RedirectStandardInput = true", webSurfaceClient);
        Assert.Contains("process.Exited", webSurfaceClient);
        Assert.Contains("Kill(entireProcessTree: true)", webSurfaceClient);
        Assert.Contains("MaximumHostFrameLength", webSurfaceClient);
        Assert.Contains("protocolVersion", webSurfaceClient);
        Assert.Contains("TryMoveFocus", webSurfaceClient);
        Assert.Contains("WebSurfaceOcclusion", shellChrome);
        Assert.Contains("<OutputType>WinExe</OutputType>", hostProject);
        Assert.Contains("Microsoft.Web.WebView2", hostProject);
        Assert.Contains("CoreWebView2Environment.CreateAsync", hostWindow);
        Assert.Contains("CreateCoreWebView2ControllerAsync", hostWindow);
        Assert.Contains("ProcessFailed", hostWindow);
        Assert.Contains("WebResourceRequested", hostWindow);
        Assert.Contains("CoreWebView2PermissionState.Deny", hostWindow);
        Assert.Contains("_allowedOrigins", hostWindow);
        Assert.Contains("IsOriginAllowed", hostWindow);
        Assert.Contains("_toolId", hostWindow);
        Assert.Contains("GetWindowThreadProcessId", hostWindow);
        Assert.Contains("AcceleratorKeyPressed", hostWindow);
        Assert.Contains("MoveFocusRequested", hostWindow);
        Assert.Contains("SetWindowRgn", hostWindow);
        Assert.Contains("_controller.IsVisible = false;", hostWindow);
        Assert.Contains("PaintOpaquePlaceholder", hostWindow);
        Assert.Contains("BeginInvoke(RevealSurfaceIfReady)", hostWindow);
        Assert.DoesNotContain("_controller.IsVisible = command.Visible", hostWindow);
        Assert.Contains("--parent-hwnd", hostProgram);
        Assert.Contains("--parent-pid", hostProgram);
        Assert.Contains("--source", hostProgram);
        Assert.Contains("--allowed-origin", hostProgram);
        Assert.Contains("--isolation-probe", hostProgram);
        Assert.Contains("--isolation-crash-probe", hostProgram);
    }

    [Theory]
    [InlineData("Ctrl+Shift+P")]
    [InlineData("Ctrl+R")]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("F5")]
    [InlineData("Escape")]
    [InlineData("Ctrl+1")]
    [InlineData("Ctrl+6")]
    public void Web_host_global_shortcuts_use_the_shell_resolver(string gesture)
    {
        Assert.True(ShellKeyboardShortcut.TryParseGesture(gesture, out var key, out var modifiers));
        Assert.NotEqual(Key.None, key);
        Assert.NotEqual(ShellKeyboardAction.None, ShellKeyboardShortcut.Resolve(key, modifiers).Action);
    }

    [Fact]
    public void Shell_and_web_host_manifests_declare_modern_Windows()
    {
        var projectDirectory = Path.Combine(
            Root,
            "src",
            "MyPowerTools.Shell.Avalonia");
        var project = File.ReadAllText(Path.Combine(
            projectDirectory,
            "MyPowerTools.Shell.Avalonia.csproj"));
        var manifest = File.ReadAllText(Path.Combine(projectDirectory, "app.manifest"));

        // The ApplicationManifest element may carry a Condition attribute for
        // non-Windows build hosts, so match the closing half of the element.
        Assert.Contains("app.manifest</ApplicationManifest>", project);
        Assert.Contains("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}", manifest);
        Assert.Contains("<requestedExecutionLevel level=\"asInvoker\"", manifest);
        Assert.Contains("PerMonitorV2", manifest);
        var hostManifest = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.WebToolHost",
            "app.manifest"));
        var hostProject = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.WebToolHost",
            "MyPowerTools.WebToolHost.csproj"));
        Assert.Contains("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}", hostManifest);
        Assert.Contains("<requestedExecutionLevel level=\"asInvoker\"", hostManifest);
        Assert.Contains("<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>", hostProject);
    }

    [Fact]
    public void Shell_build_places_the_isolated_host_beside_the_product()
    {
        var project = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Shell.Avalonia",
            "MyPowerTools.Shell.Avalonia.csproj"));

        Assert.Contains("MyPowerTools.WebToolHost.csproj", project);
        Assert.Contains("ReferenceOutputAssembly=\"false\"", project);
        Assert.Contains("SkipGetTargetFrameworkProperties=\"true\"", project);
        Assert.Contains("CopyWebToolHostForLocalRun", project);
        Assert.Contains("WebToolHost\\%(RecursiveDir)", project);
        Assert.Contains("<RemoveDir Directories=\"$(OutDir)WebToolHost\"", project);
        Assert.Contains("$(WebToolHostBuildOutput)win-x64\\**\\*.*", project);
        Assert.Contains("PublishWebToolHost", project);
        Assert.Contains("$(PublishDir)WebToolHost", project);
        Assert.Contains("Private=\"false\"", project);
        Assert.DoesNotContain("PackageReference Include=\"Microsoft.Web.WebView2\"", project);
        var publishScript = File.ReadAllText(Path.Combine(Root, "scripts", "publish-windows.ps1"));
        Assert.Contains("MyPowerTools.Shell.Avalonia\\MyPowerTools.Shell.Avalonia.csproj", publishScript);
    }

    [Fact]
    public void Source_task_recovery_is_hidden_and_targets_the_installed_task()
    {
        var serviceSource = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "smartbird-thermostat",
            "current-integration",
            "src",
            "SmartBird.Surface",
            "Services",
            "SmartBirdThermostatToolService.cs"));

        Assert.Equal("SmartBirdThermostat", SmartBirdThermostatToolService.ScheduledTaskName);
        Assert.Equal(new Uri("http://127.0.0.1:19002/"), SmartBirdThermostatToolService.DefaultBaseUri);
        Assert.Contains("CreateNoWindow = true", serviceSource);
        Assert.Contains("startInfo.ArgumentList.Add(\"/Run\")", serviceSource);
        Assert.Contains("startInfo.ArgumentList.Add(ScheduledTaskName)", serviceSource);
    }

    [Fact]
    public void Module_uses_source_backed_logs_task_restart_and_energy_endpoint()
    {
        var module = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "smartbird-thermostat",
            "current-integration",
            "src",
            "SmartBirdThermostat.MyPowerTools",
            "SmartBirdThermostatModule.cs"));

        Assert.Contains("http://127.0.0.1:18988", module);
        Assert.Contains("http://127.0.0.1:19002", module);
        Assert.Contains("/api/status", module);
        Assert.Contains("/api/events", module);
        Assert.Contains("/api/energy/status", module);
        Assert.Contains("must be a loopback HTTP URL without credentials", module);
        Assert.Contains("smartbird_thermostat_service.log", module);
        Assert.Contains("SmartBirdThermostat", module);
        Assert.Contains("RunScheduledTaskCommandAsync", module);
        Assert.Contains("[\"/Run\", \"/TN\", options.ScheduledTaskName]", module);
        Assert.DoesNotContain("requiresElevation: true", module, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_rejects_non_loopback_facade_overrides()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(
                Path.GetTempPath(),
                "mpt-smartbird-origin-test",
                Guid.NewGuid().ToString("N"))),
            [host]);
        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);

        var result = await runtime.ExecuteCommandAsync(
            new MptCommandRequest(
                "smartbird-reject-external-origin",
                "smartbird-thermostat.config.save",
                new JsonObject { ["baseUrl"] = "https://example.com/thermostat" }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(MptErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.Contains("baseUrl is fixed", result.Error.Message);
        Assert.Contains(SmartBirdThermostatModule.CanonicalBaseUrl, result.Error.Message);
    }

    [Fact]
    public async Task Module_schema_marks_source_dashboard_and_task_as_canonical_read_only_values()
    {
        var module = await CreateSettingsModuleAsync();
        try
        {
            var schema = await module.GetSettingsSchemaAsync(CancellationToken.None);
            var document = Assert.IsType<JsonObject>(JsonNode.Parse(schema.SchemaJson));
            var properties = Assert.IsType<JsonObject>(document["properties"]);

            AssertCanonicalReadOnlySetting(
                Assert.IsType<JsonObject>(properties["baseUrl"]),
                SmartBirdThermostatModule.CanonicalBaseUrl);
            AssertCanonicalReadOnlySetting(
                Assert.IsType<JsonObject>(properties["scheduledTaskName"]),
                SmartBirdThermostatModule.CanonicalScheduledTaskName);
        }
        finally
        {
            await module.DisposeAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Module_validation_rejects_every_noncanonical_source_dashboard_or_task_value()
    {
        var module = await CreateSettingsModuleAsync();
        try
        {
            var rejected = await module.ValidateSettingsAsync(
                new MyPowerTools.Abstractions.SettingsPatch(
                    module.Id,
                    1,
                    new JsonObject
                    {
                        ["baseUrl"] = "http://localhost:19002",
                        ["scheduledTaskName"] = "SmartBirdThermostat-Other"
                    }),
                CancellationToken.None);

            Assert.False(rejected.Ok);
            Assert.Contains(rejected.Messages, message =>
                message.Contains(SmartBirdThermostatModule.CanonicalBaseUrl, StringComparison.Ordinal));
            Assert.Contains(rejected.Messages, message =>
                message.Contains(SmartBirdThermostatModule.CanonicalScheduledTaskName, StringComparison.Ordinal));

            var accepted = await module.ValidateSettingsAsync(
                new MyPowerTools.Abstractions.SettingsPatch(
                    module.Id,
                    1,
                    new JsonObject
                    {
                        ["baseUrl"] = SmartBirdThermostatModule.CanonicalBaseUrl,
                        ["scheduledTaskName"] = SmartBirdThermostatModule.CanonicalScheduledTaskName
                    }),
                CancellationToken.None);

            Assert.True(accepted.Ok);
        }
        finally
        {
            await module.DisposeAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Module_full_snapshot_forces_canonical_source_values_and_applies_other_settings()
    {
        var module = await CreateSettingsModuleAsync();
        try
        {
            var applied = await module.ApplySettingsAsync(
                new MyPowerTools.Abstractions.SettingsSnapshotDocument(
                    module.Id,
                    17,
                    new JsonObject
                    {
                        ["baseUrl"] = "http://127.0.0.1:29999",
                        ["scheduledTaskName"] = "HijackedTask",
                        ["statusPath"] = "/api/custom-status",
                        ["targetTemperatureC"] = 52.5,
                        ["pollIntervalSeconds"] = 45,
                        ["eventLimit"] = 40,
                        ["notifyOnAlarm"] = false
                    },
                    DateTimeOffset.UtcNow),
                CancellationToken.None);

            Assert.Equal(17UL, applied.Revision);
            Assert.Equal(SmartBirdThermostatModule.CanonicalBaseUrl, applied.Values["baseUrl"]!.GetValue<string>());
            Assert.Equal(SmartBirdThermostatModule.CanonicalScheduledTaskName, applied.Values["scheduledTaskName"]!.GetValue<string>());
            Assert.Equal("/api/custom-status", applied.Values["statusPath"]!.GetValue<string>());
            Assert.Equal(52.5, applied.Values["targetTemperatureC"]!.GetValue<double>());
            Assert.Equal(45, applied.Values["pollIntervalSeconds"]!.GetValue<int>());
            Assert.Equal(40, applied.Values["eventLimit"]!.GetValue<int>());
            Assert.False(applied.Values["notifyOnAlarm"]!.GetValue<bool>());

            var persisted = await module.GetSettingsAsync(CancellationToken.None);
            Assert.Equal(SmartBirdThermostatModule.CanonicalBaseUrl, persisted.Values["baseUrl"]!.GetValue<string>());
            Assert.Equal(SmartBirdThermostatModule.CanonicalScheduledTaskName, persisted.Values["scheduledTaskName"]!.GetValue<string>());
            Assert.Equal("/api/custom-status", persisted.Values["statusPath"]!.GetValue<string>());
            Assert.Equal(52.5, persisted.Values["targetTemperatureC"]!.GetValue<double>());
        }
        finally
        {
            await module.DisposeAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Module_redacts_source_adb_endpoints_without_hiding_loopback_services()
    {
        var method = typeof(SmartBirdThermostatModule).GetMethod(
            "RedactSensitive",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var output = Assert.IsType<string>(method.Invoke(
            null,
            ["sensor=VIRTUAL-SKIN@192.168.29.79:35559 " +
             "dashboard=http://127.0.0.1:19002 " +
             "{\"adb_serial\": \"usb-device-serial,10.33.0.243:5555\"}"]));

        Assert.DoesNotContain("192.168.29.79", output);
        Assert.DoesNotContain("10.33.0.243", output);
        Assert.DoesNotContain("usb-device-serial", output);
        Assert.Contains("<device-endpoint>", output);
        Assert.Contains("<adb-device-list>", output);
        Assert.Contains("127.0.0.1:19002", output);
    }

    [Fact]
    public void Sdk_web_surface_session_is_reused_reloaded_replaced_and_disposed()
    {
        var service = new FakeWebSurfaceService();
        var states = new List<MptWebSurfaceState>();
        Control? embeddedView = null;
        var firstSource = new Uri("http://127.0.0.1:19002/");
        var secondSource = new Uri("http://127.0.0.1:19003/");

        using (var controller = new SmartBirdWebSurfaceSessionController(
                   service,
                   "smartbird-thermostat",
                   "overview",
                   view => embeddedView = view,
                   (state, _) => states.Add(state)))
        {
            controller.SetSource(firstSource);
            var first = Assert.Single(service.Sessions);
            Assert.Equal("smartbird-thermostat", Assert.Single(service.Requests).ToolId);
            Assert.Equal(firstSource, controller.Source);
            Assert.Null(embeddedView);

            first.Raise(MptWebSurfaceState.Ready, "ready");
            controller.Reload(firstSource);
            Assert.Equal(1, first.ReloadCount);
            Assert.Single(service.Sessions);

            controller.SetSource(secondSource);
            Assert.True(first.IsDisposed);
            Assert.Equal(2, service.Sessions.Count);
            Assert.Equal(secondSource, controller.Source);
        }

        Assert.True(service.Sessions[1].IsDisposed);
        Assert.Contains(MptWebSurfaceState.Loading, states);
        Assert.Contains(MptWebSurfaceState.Ready, states);
    }

    [Fact]
    public void Missing_sdk_web_surface_capability_keeps_the_browser_fallback_idle()
    {
        var stateChanges = 0;
        using var controller = new SmartBirdWebSurfaceSessionController(
            null,
            "smartbird-thermostat",
            "overview",
            _ => throw new InvalidOperationException("No embedded view should be installed."),
            (_, _) => stateChanges++);

        controller.SetSource(new Uri("http://127.0.0.1:19002/"));
        controller.Reload(new Uri("http://127.0.0.1:19002/"));

        Assert.Null(controller.Source);
        Assert.Equal(0, stateChanges);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static async Task<SmartBirdThermostatModule> CreateSettingsModuleAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "mpt-smartbird-fixed-settings-test",
            Guid.NewGuid().ToString("N"));
        var module = new SmartBirdThermostatModule();
        await module.InitializeAsync(
            new MyPowerTools.Abstractions.ModuleContext(
                "test-host",
                "1.0",
                module.PackageId,
                module.Id,
                Path.Combine(root, "data"),
                Path.Combine(root, "cache"),
                Path.Combine(root, "logs"),
                PlatformId.Current().Rid,
                []),
            CancellationToken.None);
        return module;
    }

    private static void AssertCanonicalReadOnlySetting(JsonObject property, string expected)
    {
        Assert.Equal(expected, property["const"]!.GetValue<string>());
        Assert.Equal(expected, property["default"]!.GetValue<string>());
        Assert.True(property["readOnly"]!.GetValue<bool>());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Unable to find MyPowerTools repository root.");
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeWebSurfaceService : IMptWebSurfaceService
    {
        public List<MptWebSurfaceRequest> Requests { get; } = [];
        public List<FakeWebSurfaceSession> Sessions { get; } = [];

        public IMptWebSurfaceSession CreateSession(MptWebSurfaceRequest request)
        {
            Requests.Add(request);
            var session = new FakeWebSurfaceSession();
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class FakeWebSurfaceSession : IMptWebSurfaceSession
    {
        public Control View => null!;
        public MptWebSurfaceState State { get; private set; } = MptWebSurfaceState.Loading;
        public int ReloadCount { get; private set; }
        public bool IsDisposed { get; private set; }
        public event EventHandler<MptWebSurfaceStateChangedEventArgs>? StateChanged;

        public void Reload() => ReloadCount++;

        public void Raise(MptWebSurfaceState state, string message)
        {
            State = state;
            StateChanged?.Invoke(this, new MptWebSurfaceStateChangedEventArgs(state, message));
        }

        public void Dispose() => IsDisposed = true;
    }
}
