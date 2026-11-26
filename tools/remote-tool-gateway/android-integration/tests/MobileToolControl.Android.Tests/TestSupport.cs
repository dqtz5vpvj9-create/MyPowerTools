using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;
using MobileToolControl.Android;

namespace MobileToolControl.Android.Tests;

/// <summary>
/// Temporary roots for this test assembly. The repository forbids <c>/tmp</c>; a restricted sandbox
/// uses the registered <c>artifacts/.tmp-android-verify</c> directory instead, and
/// <c>/mnt/cache/data-cache</c> is used when the environment explicitly provides it.
/// </summary>
internal static class TestPaths
{
    private static readonly string Root = ResolveRoot();

    public static string NewRoot()
    {
        var path = Path.Combine(Root, "mobile-tool-control-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable("MPT_TEST_TMP");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured);
            return configured;
        }

        const string cacheRoot = "/mnt/cache/data-cache";
        if (Directory.Exists(cacheRoot))
        {
            try
            {
                var probe = Path.Combine(cacheRoot, ".mpt-tool-control-probe");
                Directory.CreateDirectory(probe);
                Directory.Delete(probe);
                return cacheRoot;
            }
            catch (Exception)
            {
                // Not writable in this sandbox; fall through to the repository verify directory.
            }
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
        {
            directory = directory.Parent;
        }

        var repoRoot = directory?.FullName ?? AppContext.BaseDirectory;
        var verify = Path.Combine(repoRoot, "artifacts", ".tmp-android-verify", "mobile-tool-control-tests");
        Directory.CreateDirectory(verify);
        return verify;
    }
}

/// <summary>In-memory credential store; records what the module asked it to keep.</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Values => _values;

    public Task<SecretReference> SaveAsync(string moduleId, string name, string secret, CancellationToken cancellationToken)
    {
        var reference = SecretReference.Create(moduleId, name);
        _values[reference.Uri] = secret;
        return Task.FromResult(reference);
    }

    public Task<string?> ReadAsync(SecretReference reference, CancellationToken cancellationToken) =>
        Task.FromResult(_values.TryGetValue(reference.Uri, out var value) ? value : null);

    public Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        _values.Remove(reference.Uri);
        return Task.CompletedTask;
    }

    public void Clear() => _values.Clear();
}

/// <summary>
/// Test-only address policy. It accepts loopback so the real <see cref="HttpClient"/> can be driven
/// against <see cref="GatewayDouble"/>; production always uses <c>TailnetEndpointPolicy</c> and no
/// setting, preference or command argument can select this one.
/// </summary>
internal sealed class LoopbackEndpointPolicy : IMobileToolEndpointPolicy
{
    public bool Allows(IPAddress address) => IPAddress.IsLoopback(address);

    public string Rule => "loopback（仅测试替身）";
}

internal sealed record RecordedRequest(string Method, string Path, string Authorization, string Body, DateTimeOffset Time);

internal sealed record GatewayResponse(int Status, string Body, string? Location = null, string ContentType = "application/json");

/// <summary>
/// A real HTTP gateway double on 127.0.0.1. Tests exercise the shipped <see cref="HttpClient"/> path
/// (headers, status handling, body parsing, redirect refusal) instead of a mocked transport, and the
/// recorded requests prove what the module did or did not send.
/// </summary>
internal sealed class GatewayDouble : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    private GatewayDouble(int port)
    {
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string Endpoint => $"http://127.0.0.1:{Port}";

    public string Token { get; set; } = "test-grant-token";

    public bool RequireBearer { get; set; } = true;

    /// <summary>Optional responder override; when unset the built-in routing below is used.</summary>
    public Func<RecordedRequest, GatewayResponse>? Responder { get; set; }

    public GatewayResponse? OverrideResponse { get; set; }

    /// <summary>Artificial response delay, used to keep a request in flight while a test cancels it.</summary>
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

    public JsonObject Catalog { get; set; } = new()
    {
        ["device"] = new JsonObject { ["name"] = "工作电脑", ["platform"] = "windows" },
        ["tools"] = new JsonArray(),
        ["commands"] = new JsonArray()
    };

    public ConcurrentDictionary<string, JsonObject> Invocations { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    public int RequestCount => _requests.Count;

    public static GatewayDouble Start()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            try
            {
                return new GatewayDouble(port);
            }
            catch (HttpListenerException)
            {
                // Port raced with another process; try the next free one.
            }
        }

        throw new InvalidOperationException("无法在回环地址上启动测试网关。");
    }

    public void Reset() => _requests.Clear();

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        string body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        var recorded = new RecordedRequest(
            request.HttpMethod,
            request.Url?.AbsolutePath ?? "",
            request.Headers["Authorization"] ?? "",
            body,
            DateTimeOffset.UtcNow);
        _requests.Enqueue(recorded);

        var response = OverrideResponse ?? Responder?.Invoke(recorded) ?? BuildResponse(recorded);
        if (ResponseDelay > TimeSpan.Zero)
        {
            await Task.Delay(ResponseDelay).ConfigureAwait(false);
        }
        if (RequireBearer && !string.Equals(recorded.Authorization, "Bearer " + Token, StringComparison.Ordinal))
        {
            response = new GatewayResponse(401, "{\"error\":{\"code\":\"unauthorized\",\"message\":\"bad token\"}}");
        }

        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;
        if (response.Location is not null)
        {
            context.Response.Headers["Location"] = response.Location;
        }

        var bytes = Encoding.UTF8.GetBytes(response.Body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private GatewayResponse BuildResponse(RecordedRequest request)
    {
        var path = request.Path;
        if (path.EndsWith("/catalog", StringComparison.Ordinal))
        {
            return new GatewayResponse(200, Catalog.ToJsonString());
        }

        if (path.EndsWith("/invocations", StringComparison.Ordinal))
        {
            var document = JsonNode.Parse(request.Body) as JsonObject ?? new JsonObject();
            var invocationId = document["invocationId"]?.GetValue<string>() ?? "";
            var commandId = document["commandId"]?.GetValue<string>() ?? "";
            var invocation = new JsonObject
            {
                ["invocationId"] = invocationId,
                ["commandId"] = commandId,
                ["state"] = "running",
                ["message"] = "已提交",
                ["terminal"] = false,
                ["result"] = null
            };
            Invocations[invocationId] = invocation;
            return new GatewayResponse(202, invocation.ToJsonString());
        }

        if (path.EndsWith("/cancel", StringComparison.Ordinal))
        {
            var invocationId = path.Split('/')[^2];
            var invocation = Invocations.TryGetValue(invocationId, out var known)
                ? known
                : new JsonObject { ["invocationId"] = invocationId, ["commandId"] = "", ["state"] = "unknown" };
            var cancelled = invocation.DeepClone().AsObject();
            cancelled["state"] = "cancelled";
            cancelled["message"] = "已取消";
            cancelled["terminal"] = true;
            // The real gateway reports the runtime's own cancel answer on both spellings.
            cancelled["accepted"] = true;
            cancelled["cancelAccepted"] = true;
            Invocations[invocationId] = cancelled;
            return new GatewayResponse(200, cancelled.ToJsonString());
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var id = segments.Length >= 2 ? segments[^1] : "";
        if (Invocations.TryGetValue(id, out var existing))
        {
            return new GatewayResponse(200, existing.ToJsonString());
        }

        return new GatewayResponse(404, "{\"error\":{\"code\":\"not_found\",\"message\":\"no such invocation\"}}");
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
            // Closing an already-stopped listener is fine.
        }

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The accept loop ends with the listener.
        }

        _cts.Dispose();
    }
}

/// <summary>Builds an initialized module against a temporary data directory and fake secret store.</summary>
internal sealed class ModuleHarness : IAsyncDisposable
{
    private ModuleHarness(MobileToolControlModule module, FakeSecretStore secrets, string root)
    {
        Module = module;
        Secrets = secrets;
        Root = root;
    }

    public MobileToolControlModule Module { get; }

    public FakeSecretStore Secrets { get; }

    public string Root { get; }

    public string DataDirectory => Path.Combine(Root, "data");

    public string DevicesPath => Path.Combine(DataDirectory, "devices.json");

    public static async Task<ModuleHarness> CreateAsync(IMobileToolEndpointPolicy? policy = null)
    {
        var root = TestPaths.NewRoot();
        var data = Path.Combine(root, "data");
        var cache = Path.Combine(root, "cache");
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(logs);

        var secrets = new FakeSecretStore();
        var module = new MobileToolControlModule();
        if (policy is not null)
        {
            module.EndpointPolicy = policy;
        }

        var context = new ModuleContext(
            "test-host",
            "1.0",
            MobileToolControlOptions.PackageId,
            MobileToolControlOptions.ModuleId,
            data,
            cache,
            logs,
            "android-arm64",
            ["secret.store"],
            new Dictionary<string, object>(StringComparer.Ordinal) { ["secret.store"] = secrets });
        var result = await module.InitializeAsync(context, CancellationToken.None);
        Assert.True(result.Ok);

        return new ModuleHarness(module, secrets, root);
    }

    public Task<CommandExecutionResult> RunAsync(string commandId, JsonObject? args = null) =>
        Module.ExecuteCommandAsync(
            new CommandRequest(Guid.NewGuid().ToString("N"), commandId, args ?? new JsonObject()),
            CancellationToken.None).AsTask();

    public static JsonObject Payload(CommandExecutionResult result) =>
        string.IsNullOrWhiteSpace(result.Output)
            ? new JsonObject()
            : JsonNode.Parse(result.Output) as JsonObject ?? new JsonObject();

    public async Task<JsonObject> ImportAsync(GatewayDouble gateway, string grantId = "grant-1", string deviceName = "工作电脑")
    {
        var code = BuildCode(gateway.Endpoint, gateway.Token, grantId, deviceName);
        var preview = await RunAsync(
            MobileToolControlOptions.CommandImportPreview,
            new JsonObject { [MobileToolControlOptions.ArgumentCode] = code });
        Assert.True(preview.Success);
        Assert.True(Payload(preview)["ok"]!.GetValue<bool>());

        var confirm = await RunAsync(
            MobileToolControlOptions.CommandImportConfirm,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentCode] = code,
                [MobileToolControlOptions.ArgumentAccepted] = true
            });
        Assert.True(confirm.Success, confirm.Error?.Message);
        return Payload(confirm);
    }

    public static string BuildCode(string endpoint, string token, string grantId, string deviceName)
    {
        var json = new JsonObject
        {
            ["version"] = 1,
            ["endpoint"] = endpoint,
            ["grantId"] = grantId,
            ["deviceName"] = deviceName,
            ["token"] = token
        }.ToJsonString();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"mpt://control/{payload}";
    }

    public async ValueTask DisposeAsync()
    {
        await Module.DisposeAsync(CancellationToken.None);
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }
}
