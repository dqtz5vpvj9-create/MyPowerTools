using System.Text.Json;

namespace FileTransfer.Core.Discovery;

/// <summary>
/// Outcome of reading the tailnet state. <see cref="Status"/> is null when neither the CLI nor the
/// LocalAPI could answer; <see cref="Diagnostics"/> then names every path that was tried and why it
/// failed, because "no Tailscale here" must be visible, never silently turned into "no devices".
/// </summary>
public sealed record TailscaleReadResult(TailscaleStatus? Status, IReadOnlyList<string> Diagnostics)
{
    public bool Available => Status is not null;
}

/// <summary>
/// Reads <c>tailscale status --json</c> through the platform's CLI paths and falls back to the
/// LocalAPI socket. Both come from <see cref="TailscaleEnvironment"/>, so Windows, macOS and Linux use
/// their real install layouts and Android/iOS report that they have no source at all.
/// <para>
/// Commands are bounded: the CLI gets one timeout, no shell is used, and every path is tried at most
/// once. On macOS a second, explicit <c>--socket</c> attempt covers the App Store build, whose CLI does
/// not dial the default socket.
/// </para>
/// </summary>
public sealed class TailscaleStatusReader
{
    private readonly TailscaleEnvironment _environment;
    private readonly IProcessRunner _runner;
    private readonly ITailscaleLocalApiTransport _localApi;
    private readonly TimeSpan _timeout;

    public TailscaleStatusReader(
        TailscaleEnvironment? environment = null,
        IProcessRunner? runner = null,
        ITailscaleLocalApiTransport? localApi = null,
        TimeSpan? timeout = null)
    {
        _environment = environment ?? TailscaleEnvironment.Detect();
        _runner = runner ?? new ProcessRunner();
        _localApi = localApi ?? new TailscaleLocalApiTransport();
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public TailscaleEnvironment Environment => _environment;

    public async Task<TailscaleReadResult> ReadAsync(CancellationToken token)
    {
        var diagnostics = new List<string>();
        if (!_environment.Supported)
        {
            diagnostics.Add($"{_environment.Platform} 上没有可用的 Tailscale CLI 或 LocalAPI，无法读取 Tailnet 设备列表。");
            return new TailscaleReadResult(null, diagnostics);
        }

        var attempts = new List<(string Path, string[] Arguments)>();
        foreach (var path in _environment.CliPaths) attempts.Add((path, ["status", "--json"]));
        // The App Store build's CLI does not dial the default socket, so retry the first CLI once per
        // socket path. The list stays short: one window must not spend minutes on CLI attempts.
        if (_environment.Platform == "macos")
            foreach (var socket in _environment.LocalApiPaths.Take(2))
                foreach (var path in _environment.CliPaths.Take(1))
                    attempts.Add((path, ["--socket", socket, "status", "--json"]));

        foreach (var attempt in attempts)
        {
            token.ThrowIfCancellationRequested();
            ProcessResult result;
            try
            {
                result = await _runner.RunAsync(attempt.Path, attempt.Arguments, _timeout, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                diagnostics.Add($"Tailscale CLI（{attempt.Path}）无法运行：{Shorten(ex.Message)}");
                continue;
            }
            // A path that does not exist is not an error worth reporting: it is just not installed there.
            if (!result.Started) continue;
            if (result.ExitCode != 0 || result.StandardOutput.Trim().Length == 0)
            {
                diagnostics.Add($"Tailscale CLI（{attempt.Path}）没有返回状态：{Shorten(result.Error)}");
                continue;
            }
            if (TryParse(result.StandardOutput, $"Tailscale CLI（{attempt.Path}）", diagnostics, out var status))
                return new TailscaleReadResult(status, diagnostics);
        }

        foreach (var socket in _environment.LocalApiPaths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var json = await _localApi.GetStatusAsync(socket, token);
                if (TryParse(json, $"Tailscale LocalAPI（{socket}）", diagnostics, out var status))
                    return new TailscaleReadResult(status, diagnostics);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                diagnostics.Add($"Tailscale LocalAPI（{socket}）不可用：{Shorten(TailscaleLocalApiTransport.Describe(ex))}");
            }
        }

        diagnostics.Insert(0, _environment.CliPaths.Count == 0
            ? "这台设备没有 Tailscale CLI，也没有可用的 LocalAPI；本次没有 Tailnet 候选。"
            : "没有找到可用的 Tailscale CLI，LocalAPI 也不可用；本次没有 Tailnet 候选。");
        return new TailscaleReadResult(null, diagnostics);
    }

    private static bool TryParse(string json, string source, List<string> diagnostics, out TailscaleStatus status)
    {
        try
        {
            status = TailscaleStatusParser.Parse(json);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            status = null!;
            diagnostics.Add($"{source} 的输出无法解析：{Shorten(ex.Message)}");
            return false;
        }
    }

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }
}
