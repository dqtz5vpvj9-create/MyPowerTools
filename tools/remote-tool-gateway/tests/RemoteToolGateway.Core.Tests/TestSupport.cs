using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using RemoteToolGateway.Core;

namespace RemoteToolGateway.Core.Tests;

/// <summary>
/// Injected executor for the gateway tests. It never touches the real runtime; every assertion
/// about "did this command run" is answered by <see cref="Executions"/>.
/// </summary>
internal sealed class FakeHostControlBridge : IHostControlBridge
{
    private readonly object _lock = new();
    private readonly List<Execution> _executions = [];

    public List<HostToolDescriptor> Tools { get; } = [];
    public List<HostCommandDescriptor> Commands { get; } = [];
    public bool PingResult { get; set; } = true;
    public bool CatalogFails { get; set; }
    public Func<string, string, JsonObject, CancellationToken, IAsyncEnumerable<HostExecutionEvent>>? Handler { get; set; }
    public Func<string, HostCancellation>? CancelHandler { get; set; }
    public List<string> Cancellations { get; } = [];

    /// <summary>Set to block the catalog read; the test decides when the runtime answers.</summary>
    public TaskCompletionSource? CatalogGate { get; set; }
    private TaskCompletionSource _catalogRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _cancelRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when a catalog read has started, so a test can act while it is blocked.</summary>
    public TaskCompletionSource CatalogRequested => _catalogRequested;
    /// <summary>Set to block the runtime's cancel answer.</summary>
    public TaskCompletionSource? CancelGate { get; set; }
    /// <summary>Completes when a cancel arrived, so a test can act while it is blocked.</summary>
    public TaskCompletionSource CancelRequested => _cancelRequested;

    /// <summary>Fresh signals, so a test can observe the next call rather than an earlier one.</summary>
    public void ResetSignals()
    {
        _catalogRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cancelRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed record Execution(string InvocationId, string CommandId, JsonObject Args);

    public IReadOnlyList<Execution> Executions
    {
        get { lock (_lock) return _executions.ToArray(); }
    }

    public int ExecutionCount
    {
        get { lock (_lock) return _executions.Count; }
    }

    public async Task<HostCatalog> GetCatalogAsync(CancellationToken cancellationToken)
    {
        _catalogRequested.TrySetResult();
        if (CatalogGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        if (CatalogFails) throw new InvalidOperationException("runtime offline");
        return new HostCatalog(Tools.ToArray(), Commands.ToArray());
    }

    public async IAsyncEnumerable<HostExecutionEvent> ExecuteStreamAsync(
        string invocationId,
        string commandId,
        JsonObject args,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (_lock) _executions.Add(new Execution(invocationId, commandId, (JsonObject)args.DeepClone()));
        if (Handler is not null)
        {
            await foreach (var item in Handler(invocationId, commandId, args, cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        yield return new HostExecutionEvent(invocationId, commandId, "succeeded", "已执行。", true,
            new ControlInvocationResult(invocationId, "succeeded", "已执行。", "", "", "", false, null));
    }

    public async Task<HostCancellation> CancelAsync(string invocationId, CancellationToken cancellationToken)
    {
        lock (_lock) Cancellations.Add(invocationId);
        _cancelRequested.TrySetResult();
        if (CancelGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        return CancelHandler?.Invoke(invocationId)
            ?? new HostCancellation(true, invocationId, "cancelling", "运行时已接受取消。");
    }

    public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(PingResult);

    public FakeHostControlBridge WithCommand(
        string commandId,
        string moduleId = "demo",
        string title = "",
        string dangerLevel = "",
        bool requiresElevation = false,
        JsonObject? execution = null,
        IReadOnlyList<string>? constraints = null)
    {
        Commands.Add(new HostCommandDescriptor(
            commandId,
            moduleId,
            title.Length > 0 ? title : commandId,
            moduleId,
            dangerLevel,
            requiresElevation,
            false,
            true,
            [],
            constraints ?? [],
            execution));
        return this;
    }

    public FakeHostControlBridge WithTool(string toolId, string moduleId = "demo", string title = "")
    {
        Tools.Add(new HostToolDescriptor(toolId, moduleId, title.Length > 0 ? title : toolId, "", "Demo", "running", "available"));
        return this;
    }
}

internal static class TestPaths
{
    /// <summary>
    /// Test scratch space. The shared cache is preferred, the registered sandbox directory is the
    /// fallback, and /tmp is deliberately never used.
    /// </summary>
    public static string NewDirectory(string prefix)
    {
        var configured = Environment.GetEnvironmentVariable("MPT_RTG_TEST_TMP");
        string root;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            root = configured;
        }
        else
        {
            const string cache = "/mnt/cache/data-cache/mpt-remote-tool-gateway-tests";
            root = IsWritable(cache)
                ? cache
                : Path.Combine(FindRepositoryRoot(), "artifacts", ".tmp-android-verify", "rtg-tests");
        }

        var directory = Path.Combine(root, prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Best-effort scratch cleanup; a locked file from a disposed runtime is not a test failure.</summary>
    public static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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

    private static string FindRepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "MyPowerTools.slnx"))) return directory;
            directory = Path.GetDirectoryName(directory);
        }

        return Directory.GetCurrentDirectory();
    }
}

/// <summary>Raw HTTP client for the loopback test transport.</summary>
internal sealed class GatewayClient : IDisposable
{
    private readonly HttpClient _http;

    public GatewayClient(int port, string token)
    {
        Token = token;
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}")
        };
        _http.DefaultRequestHeaders.ExpectContinue = false;
    }

    public string Token { get; }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

    public Task<HttpResponseMessage> PostAsync(string path, string body) => SendAsync(HttpMethod.Post, path, body);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (Token.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _http.SendAsync(request);
    }

    public void Dispose() => _http.Dispose();
}

internal static class HttpAssert
{
    public static async Task<JsonObject> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) as JsonObject
            ?? throw new Xunit.Sdk.XunitException($"Response body was not a JSON object: {text}");
    }

    public static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        var json = await JsonAsync(response);
        var error = json["error"] as JsonObject;
        Assert.NotNull(error);
        Assert.False(string.IsNullOrWhiteSpace(error!["code"]?.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(error["message"]?.GetValue<string>()));
        return error["code"]!.GetValue<string>();
    }

    public static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, await ErrorCodeAsync(response));
    }

    public static string ReadString(JsonObject json, string key)
    {
        try { return json[key]?.GetValue<string>() ?? ""; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return ""; }
    }

    public static bool ReadBool(JsonObject json, string key)
    {
        try { return json[key]?.GetValue<bool>() ?? false; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return false; }
    }
}
