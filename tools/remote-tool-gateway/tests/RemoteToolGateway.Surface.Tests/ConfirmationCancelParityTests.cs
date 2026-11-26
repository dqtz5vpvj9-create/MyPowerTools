using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Platform.Abstractions;
using RemoteToolGateway.Core;
using RemoteToolGateway.Surface;

namespace RemoteToolGateway.Surface.Tests;

/// <summary>
/// Parity between the real surface adapter and the gateway: the desktop page must execute a
/// confirmed remote request under the phone's own invocation id, and a phone cancel must therefore
/// reach exactly the call the desktop started. The runtime is a fake bridge standing in for the
/// Runner, but the page talks through a real <see cref="MptAvaloniaSurfaceContext"/>, so the adapter
/// under test is the production one.
/// </summary>
public sealed class ConfirmationCancelParityTests
{
    [Fact]
    public async Task PhoneCancel_ReachesTheSameInvocationTheDesktopPageExecuted()
    {
        var bridge = new CancelAwareRuntime();
        var directory = NewDirectory();
        await using var service = new RemoteToolGatewayService(directory, new InMemorySecretStore(), bridge,
            new RemoteToolGatewayOptions
            {
                ModuleId = "remote-tool-gateway",
                DeviceName = "测试电脑",
                Platform = "linux",
                AllowLoopbackTransport = true
            });
        await service.InitializeAsync(CancellationToken.None);
        await service.StartListenerAsync("127.0.0.1", 0, CancellationToken.None);
        bridge.WithElevatedCommand("svc.restart");
        var created = await service.CreateGrantAsync("我的手机", ["svc.restart"], allowElevated: true, CancellationToken.None);
        var grantId = created["grantId"]!.GetValue<string>();
        var token = await service.Grants.ReadTokenAsync(grantId, CancellationToken.None);
        using var client = new RawGatewayClient(service.ListenerPort, token!);

        var pending = await PostAsync(client, new JsonObject
        {
            ["invocationId"] = "parity-invoke-0001",
            ["commandId"] = "svc.restart",
            ["args"] = new JsonObject { ["service"] = "Spooler" }
        });
        Assert.Equal("awaiting-confirmation", pending["state"]!.GetValue<string>());

        // The production adapter over a real surface context. Only the invocation-scoped entry point
        // is wired; a page that used the random-id entry point would fail this test loudly.
        var executedIds = new List<string>();
        var randomIdRuntimeCalls = 0;
        var context = new MptAvaloniaSurfaceContext(
            "remote-tool-gateway",
            "main",
            directory,
            "light",
            async (commandId, args, cancellationToken) =>
            {
                // Local gateway commands use the ordinary module entry point; a *runtime* command on
                // this path would mean the page had lost the phone's invocation id.
                if (commandId.StartsWith("remote-tool-gateway.", StringComparison.Ordinal))
                {
                    var payload = await ExecuteModuleCommandAsync(service, commandId, args ?? new JsonObject(), cancellationToken);
                    return new CommandExecutionResult(Guid.NewGuid().ToString("N"), commandId, "succeeded", true, payload.ToJsonString());
                }

                randomIdRuntimeCalls++;
                return new CommandExecutionResult(
                    "random-id-not-allowed", commandId, "failed", false, "",
                    new MptRuntimeError("test.random-id", "页面必须使用保留手机调用 ID 的执行入口。"));
            },
            (_, _, _) => Task.CompletedTask,
            null!,
            _ => { })
        {
            ExecuteCommandWithInvocationAsync = async (invocationId, commandId, args, cancellationToken) =>
            {
                executedIds.Add(invocationId);
                var state = "failed";
                var summary = "";
                await foreach (var evt in bridge.ExecuteStreamAsync(invocationId, commandId, args, cancellationToken))
                {
                    if (evt.FinalResponse is { } final)
                    {
                        state = final.State;
                        summary = final.Summary;
                    }
                    else if (evt.Terminal)
                    {
                        state = evt.State;
                        summary = evt.Message;
                    }
                }

                return new CommandExecutionResult(invocationId, commandId, state,
                    state is "succeeded", summary,
                    state is "succeeded" ? null : new MptRuntimeError(state, summary));
            }
        };
        var host = new MptAvaloniaSurfaceHost(context);
        Assert.True(host.SupportsInvocationScopedExecution);

        var viewModel = new ControlSurfaceViewModel(host);
        await viewModel.LoadAsync();
        Assert.Single(viewModel.Pending);
        Assert.Equal("parity-invoke-0001", viewModel.Pending[0].InvocationId);

        var confirm = viewModel.ConfirmAsync(viewModel.Pending[0]);
        await bridge.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The runtime is executing under the phone's id, so the phone's cancel finds it.
        Assert.Equal(["parity-invoke-0001"], executedIds);
        Assert.Equal(0, randomIdRuntimeCalls);
        var cancel = await PostCancelAsync(client, "parity-invoke-0001");
        Assert.True(cancel["accepted"]!.GetValue<bool>());
        Assert.Equal(["parity-invoke-0001"], bridge.Cancellations);

        await confirm;
        var final = await GetAsync(client, "parity-invoke-0001");
        Assert.True(final["terminal"]!.GetValue<bool>());
        Assert.Equal("cancelled", final["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task PageWithoutTheInvocationScopedEntryPoint_RefusesInsteadOfExecutingUnderANewId()
    {
        var directory = NewDirectory();
        var randomIdCalls = 0;
        var context = new MptAvaloniaSurfaceContext(
            "remote-tool-gateway",
            "main",
            directory,
            "light",
            (commandId, _, _) =>
            {
                randomIdCalls++;
                return Task.FromResult(new CommandExecutionResult("random", commandId, "succeeded", true, "{}"));
            },
            (_, _, _) => Task.CompletedTask,
            null!,
            _ => { });

        var host = new MptAvaloniaSurfaceHost(context);
        Assert.False(host.SupportsInvocationScopedExecution);

        var thrown = await Assert.ThrowsAsync<NotSupportedException>(() =>
            host.ExecuteRuntimeCommandAsync("inv-x", "svc.restart", new JsonObject(), CancellationToken.None));
        Assert.Contains("不支持保留手机调用 ID", thrown.Message);
        Assert.Equal(0, randomIdCalls);
    }

    /// <summary>The module's own command surface, driven directly for this in-process page.</summary>
    private static async Task<JsonObject> ExecuteModuleCommandAsync(
        RemoteToolGatewayService service,
        string commandId,
        JsonObject args,
        CancellationToken cancellationToken) => commandId switch
    {
        "remote-tool-gateway.inspect" => await service.DescribeAsync(listenerEnabled: true, includeCatalog: true, cancellationToken),
        "remote-tool-gateway.confirmation.claim" => await service.ClaimConfirmationAsync(ReadString(args, "invocationId"), cancellationToken),
        "remote-tool-gateway.confirmation.resolve" => service.ResolveConfirmation((JsonObject)args.DeepClone()),
        _ => throw new NotSupportedException(commandId)
    };

    private static string ReadString(JsonObject json, string key)
    {
        try { return json[key]?.GetValue<string>() ?? ""; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return ""; }
    }

    private static async Task<JsonObject> PostAsync(RawGatewayClient client, JsonObject body)
    {
        var response = await client.PostAsync("/mpt-control/v1/invocations", body.ToJsonString());
        // 200 = already finished, 202 = accepted but not finished (the pending-confirmation case).
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted, response.StatusCode.ToString());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static async Task<JsonObject> PostCancelAsync(RawGatewayClient client, string invocationId)
    {
        var response = await client.PostAsync($"/mpt-control/v1/invocations/{invocationId}/cancel", "");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static async Task<JsonObject> GetAsync(RawGatewayClient client, string invocationId)
    {
        var response = await client.GetAsync($"/mpt-control/v1/invocations/{invocationId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static string NewDirectory()
    {
        var root = Environment.GetEnvironmentVariable("MPT_RTG_TEST_TMP");
        if (string.IsNullOrWhiteSpace(root))
        {
            const string cache = "/mnt/cache/data-cache/mpt-remote-tool-gateway-tests";
            root = IsWritable(cache)
                ? cache
                : Path.Combine(RepositoryRoot(), "artifacts", ".tmp-android-verify", "rtg-tests");
        }

        var directory = Path.Combine(root, "surface-parity-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "MyPowerTools.slnx"))) return directory;
            directory = Path.GetDirectoryName(directory);
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>A runtime that can be cancelled: it only ends once a cancel arrives for its id.</summary>
    private sealed class CancelAwareRuntime : IHostControlBridge
    {
        private readonly List<string> _cancellations = [];
        private readonly object _lock = new();
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Cancellations
        {
            get { lock (_lock) return _cancellations.ToArray(); }
        }

        public void WithElevatedCommand(string commandId) => Commands.Add(new HostCommandDescriptor(
            commandId, "nssm-manager", "重启服务", "nssm-manager", "", true, false, true, [], [], null));

        public List<HostCommandDescriptor> Commands { get; } = [];

        public Task<HostCatalog> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HostCatalog([], Commands.ToArray()));

        public async IAsyncEnumerable<HostExecutionEvent> ExecuteStreamAsync(
            string invocationId,
            string commandId,
            JsonObject args,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            yield return new HostExecutionEvent(invocationId, commandId, "running", "正在执行。", false, null);
            await _cancelled.Task.WaitAsync(cancellationToken);
            yield return new HostExecutionEvent(invocationId, commandId, "cancelled", "调用已取消。", true,
                new ControlInvocationResult(invocationId, "cancelled", "", "", "cancelled", "调用已取消。", true, null));
        }

        public Task<HostCancellation> CancelAsync(string invocationId, CancellationToken cancellationToken)
        {
            lock (_lock) _cancellations.Add(invocationId);
            _cancelled.TrySetResult();
            return Task.FromResult(new HostCancellation(true, invocationId, "cancelling", "运行时已接受取消。"));
        }

        public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class RawGatewayClient : IDisposable
    {
        private readonly HttpClient _http;

        public RawGatewayClient(int port, string token)
        {
            _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}")
            };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + token);
            _http.DefaultRequestHeaders.ExpectContinue = false;
        }

        public Task<HttpResponseMessage> GetAsync(string path) => _http.GetAsync(path);

        public Task<HttpResponseMessage> PostAsync(string path, string body) =>
            _http.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));

        public void Dispose() => _http.Dispose();
    }
}
