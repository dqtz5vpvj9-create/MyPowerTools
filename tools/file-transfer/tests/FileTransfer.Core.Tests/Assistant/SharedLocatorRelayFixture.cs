using System.Diagnostics;
using System.Text.Json;

namespace FileTransfer.Tests;

/// <summary>Repository relay process with test-only request counters and fault markers; no production service changes.</summary>
internal sealed class SharedLocatorRelayFixture(string directory) : IAsyncDisposable
{
    private const string Launcher = """
        import json, pathlib, sys, threading
        sys.path.insert(0, sys.argv[1])
        from mpt_relay.config import Config
        from mpt_relay.service import build, setup_logging
        from mpt_relay.messages import json_response
        root = pathlib.Path(sys.argv[2])
        config = Config.load(['--host', '127.0.0.1', '--port', sys.argv[3], '--data-dir', str(root / 'data'), '--base-path', '/mpt/relay', '--log-level', 'error'])
        store, app, server = build(config, setup_logging(config))
        original = app.handle
        lock = threading.Lock()
        def observed(request):
            path = request.url_path
            auth_failure = (root / 'fail-payload-auth').exists() and request.method == 'GET' and path.endswith('/payload')
            failure = ((root / 'fail-locator').exists() and request.method == 'PUT' and '/assistant-locator/' in path and path.endswith('/manifest.json')) or ((root / 'fail-payload').exists() and request.method == 'PUT' and path.endswith('/payload')) or ((root / 'fail-manifest').exists() and request.method == 'PUT' and '/assistant/' in path and path.endswith('/manifest.json'))
            response = json_response(403, {'error': 'test-auth-rejected'}) if auth_failure else json_response(503, {'error': 'test-unavailable'}) if failure else original(request)
            with lock, (root / 'requests.jsonl').open('a') as out:
                out.write(json.dumps({'method': request.method, 'path': path, 'status': response.status, 'bytes': int(request.header('content-length') or 0), 'authenticated': bool(request.header('authorization'))}) + '\n')
            return response
        app.handle = observed
        print('READY ' + str(server.port), flush=True)
        server.start_maintenance()
        server.serve_forever()
        """;
    private Process? _process;
    private Task _stderr = Task.CompletedTask;
    public int Port { get; private set; }
    public bool ProxyEnabled { get; set; }
    public Uri? TailOrigin { get; set; }
    public Uri Address => new($"http://127.0.0.1:{Port}");
    public string DirectoryPath => directory;
    public string ContentPath(string conversationId, params string[] path) =>
        Path.Combine([directory, "data", "conversations", conversationId, .. path]);

    public async Task StartAsync()
    {
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "launch.py");
        await File.WriteAllTextAsync(script, Launcher);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "tools/file-transfer/relay/mpt_relay/service.py"))) repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("找不到真实 relay 源码。");
        var info = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(script);
        info.ArgumentList.Add(Path.Combine(repository.FullName, "tools/file-transfer/relay"));
        info.ArgumentList.Add(directory);
        info.ArgumentList.Add(Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("MPT_RELAY_", StringComparison.Ordinal)).ToArray()) info.Environment.Remove(key);
        info.Environment["MPT_RELAY_REGISTER_PER_IP_PER_HOUR"] = "1000";
        info.Environment["MPT_RELAY_REGISTER_GLOBAL_PER_HOUR"] = "5000";
        info.Environment["MPT_RELAY_AUTH_FAILURES_PER_IP"] = "0";
        info.Environment["MPT_RELAY_TAIL_PAYLOAD_PROXY"] = ProxyEnabled ? "1" : "0";
        if (TailOrigin is not null) info.Environment["MPT_RELAY_TAIL_PAYLOAD_ORIGIN"] = TailOrigin.ToString();
        _process = Process.Start(info)!;
        _stderr = PumpAsync(_process.StandardError, Path.Combine(directory, "stderr.log"));
        var ready = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        if (ready is null || !ready.StartsWith("READY ")) throw new InvalidOperationException("测试 relay 启动失败：" + ready);
        Port = int.Parse(ready[6..], System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task StopAsync()
    {
        if (_process is not { } process) return;
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        await _stderr;
        process.Dispose();
        _process = null;
    }

    public async Task RestartAsync() { await StopAsync(); await StartAsync(); }
    public void Fail(string operation, bool fail)
    {
        var marker = Path.Combine(directory, "fail-" + operation);
        if (fail) File.WriteAllText(marker, "test");
        else if (File.Exists(marker)) File.Delete(marker);
    }

    public long PayloadUploadBytes => Requests().Where(row => row.GetProperty("method").GetString() == "PUT"
        && row.GetProperty("path").GetString()!.EndsWith("/payload", StringComparison.Ordinal)
        && row.GetProperty("status").GetInt32() is >= 200 and < 300).Sum(row => row.GetProperty("bytes").GetInt64());
    public int PayloadDownloads => Requests().Count(row => row.GetProperty("method").GetString() == "GET"
        && row.GetProperty("path").GetString()!.EndsWith("/payload", StringComparison.Ordinal));
    public long MetadataUploadBytes => Requests().Where(row => row.GetProperty("method").GetString() == "PUT"
        && !row.GetProperty("path").GetString()!.EndsWith("/payload", StringComparison.Ordinal)
        && row.GetProperty("status").GetInt32() is >= 200 and < 300).Sum(row => row.GetProperty("bytes").GetInt64());
    public bool HealthReceivedCredentials => Requests().Any(row => row.GetProperty("path").GetString() == "/mpt/relay/health"
        && row.GetProperty("authenticated").GetBoolean());

    private IEnumerable<JsonElement> Requests()
    {
        var path = Path.Combine(directory, "requests.jsonl");
        if (!File.Exists(path)) yield break;
        foreach (var line in File.ReadLines(path))
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (document) yield return document.RootElement.Clone();
        }
    }

    private static async Task PumpAsync(StreamReader source, string path)
    {
        await using var target = new StreamWriter(path, append: true) { AutoFlush = true };
        while (await source.ReadLineAsync() is { } line) await target.WriteLineAsync(line);
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
