using System.Diagnostics;
using System.Text;

namespace FileTransfer.Core.Discovery;

/// <summary>Result of one bounded external command. <see cref="Started"/> is false when the executable does not exist.</summary>
public sealed record ProcessResult(bool Started, int ExitCode, string StandardOutput, string Error);

/// <summary>
/// Runs the Tailscale CLI. It is injectable so discovery tests never depend on a real installation
/// and never execute a real binary.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token);
}

/// <summary>
/// The real runner: no shell, bounded output, bounded time, and the whole process tree is killed on
/// timeout so a hung daemon client cannot keep an MPT process alive.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxOutputChars = 8 * 1024 * 1024;

    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) return new ProcessResult(false, -1, "", "无法启动进程。");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return new ProcessResult(false, -1, "", ex.Message);
        }

        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(window.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            return new ProcessResult(true, -1, await stdout, token.IsCancellationRequested ? "已取消。" : $"命令在 {timeout.TotalSeconds:0.#} 秒内没有结束。");
        }
        return new ProcessResult(true, process.ExitCode, await stdout, await stderr);
    }

    /// <summary>Reads at most <see cref="MaxOutputChars"/> characters; a runaway writer cannot exhaust memory.</summary>
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        while (builder.Length < MaxOutputChars)
        {
            var read = await reader.ReadAsync(buffer);
            if (read == 0) break;
            builder.Append(buffer, 0, Math.Min(read, MaxOutputChars - builder.Length));
        }
        return builder.ToString();
    }
}
