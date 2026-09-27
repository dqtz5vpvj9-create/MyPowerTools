using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FileTransfer.Core;

/// <summary>Owns only the OpenList process started by this tool. Nothing starts on construction.</summary>
public sealed class OpenListRuntime(string directory) : IAsyncDisposable
{
    public const string Version = "v4.2.6";
    public const int Port = 15244;
    private Process? _process;
    private Task? _stdout;
    private Task? _stderr;
    public bool Running => _process is { HasExited: false };
    public string Address { get; private set; } = "127.0.0.1";
    public string AdminUrl => $"http://{(Address.Contains(':') ? $"[{Address}]" : Address)}:{Port}/@manage";
    private string Executable => Path.Combine(directory, Version, OperatingSystem.IsWindows() ? "openlist.exe" : "openlist");

    public async Task<string?> InitializeAdminAsync(CancellationToken token)
    {
        await InstallAsync(token);
        if (File.Exists(Path.Combine(directory, "data", "data.db"))) return null;
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "admin", "random", "--data", Path.Combine(directory, "data") }) info.ArgumentList.Add(arg);
        info.Environment["OPENLIST_LOG_ENABLE"] = "false";
        using var process = Process.Start(info) ?? throw new IOException("无法初始化 OpenList。");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var text = await output;
        await error;
        var match = Regex.Match(text, @"(?m)^password:\s*(\S+)");
        if (process.ExitCode != 0 || !match.Success) throw new IOException("OpenList 管理员初始化失败。");
        return match.Groups[1].Value;
    }

    public async Task InstallAsync(CancellationToken token)
    {
        if (File.Exists(Executable)) return;
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        { Architecture.X64 => "amd64", Architecture.Arm64 => "arm64", _ => throw new PlatformNotSupportedException() };
        var suffix = OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";
        var uri = $"https://github.com/OpenListTeam/OpenList/releases/download/{Version}/openlist-{os}-{arch}{suffix}";
        var target = Path.GetDirectoryName(Executable)!;
        Directory.CreateDirectory(target);
        var archive = Path.Combine(target, "download" + suffix);
        var staging = Path.Combine(target, "extract");
        Directory.CreateDirectory(staging);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MyPowerTools-FileTransfer/0.1.0");
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                await response.Content.CopyToAsync(output, token);
            if (OperatingSystem.IsWindows()) ZipFile.ExtractToDirectory(archive, staging, true);
            else
            {
                await using var input = File.OpenRead(archive);
                await using var gzip = new GZipStream(input, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, staging, true, token);
            }
            var binary = Directory.GetFiles(staging, Path.GetFileName(Executable), SearchOption.AllDirectories).Single();
            File.Move(binary, Executable, true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public async Task StartAsync(string address, CancellationToken token)
    {
        if (Running) return;
        DirectTransfer.RequirePrivateAddress(IPAddress.Parse(address));
        await InstallAsync(token);
        Address = address;
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("server");
        info.ArgumentList.Add("--data");
        info.ArgumentList.Add(Path.Combine(directory, "data"));
        info.Environment["OPENLIST_ADDR"] = address;
        info.Environment["OPENLIST_HTTP_PORT"] = Port.ToString();
        info.Environment["OPENLIST_TLS_INSECURE_SKIP_VERIFY"] = "false";
        _process?.Dispose();
        _process = Process.Start(info) ?? throw new IOException("无法启动 OpenList。");
        // Initial OpenList logs can contain generated credentials. Never forward them into MPT logs.
        _stdout = _process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        _stderr = _process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            for (var i = 0; i < 60; i++)
            {
                token.ThrowIfCancellationRequested();
                if (_process.HasExited) throw new IOException($"OpenList 启动失败，退出码 {_process.ExitCode}。");
                try
                {
                    using var response = await http.GetAsync(new Uri(new Uri(AdminUrl), "/ping"), token);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
                await Task.Delay(500, token);
            }
            throw new IOException("OpenList 启动超时。");
        }
        catch { await StopAsync(); throw; }
    }

    public async Task StopAsync()
    {
        if (_process is null) return;
        if (!_process.HasExited)
        {
            // OpenList handles SIGTERM on Unix; Windows has no equivalent console signal here.
            if (!OperatingSystem.IsWindows())
            {
                using var stop = Process.Start(new ProcessStartInfo("/bin/kill")
                { UseShellExecute = false, ArgumentList = { "-TERM", _process.Id.ToString() } });
                if (stop is not null) await stop.WaitForExitAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { _process.Kill(true); }
            }
            else _process.Kill(true);
            await _process.WaitForExitAsync();
        }
        if (_stdout is not null) await _stdout;
        if (_stderr is not null) await _stderr;
        _process.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
