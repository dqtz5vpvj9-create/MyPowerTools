using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MyPowerTools.Packaging.Ota;

internal sealed record MacProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>A short diagnostic for error messages: stderr first, then stdout.</summary>
    public string Diagnostic
    {
        get
        {
            var text = !string.IsNullOrWhiteSpace(StandardError) ? StandardError : StandardOutput;
            text = text.Trim();
            return text.Length > 600 ? text[..600] + "…" : text;
        }
    }
}

/// <summary>
/// Runs a native tool with an explicit argument vector (no shell), a timeout and captured
/// output. A tool that cannot be started reports exit code 127, like a shell would.
/// </summary>
internal static class MacProcessRunner
{
    public static MacProcessResult Run(string fileName, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new MacProcessResult(127, string.Empty, $"无法启动 {fileName}：{exception.Message}");
        }

        if (process is null)
        {
            return new MacProcessResult(127, string.Empty, $"无法启动 {fileName}。");
        }

        using (process)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                process.WaitForExit(5000);
                return new MacProcessResult(
                    124,
                    SafeResult(standardOutput),
                    $"{Path.GetFileName(fileName)} 在 {timeout.TotalSeconds:0} 秒内未完成，已终止。");
            }

            process.WaitForExit();
            return new MacProcessResult(process.ExitCode, SafeResult(standardOutput), SafeResult(standardError));
        }
    }

    public static Task<MacProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var argumentList = arguments.ToArray();
        return Task.Run(() => Run(fileName, argumentList, timeout), cancellationToken);
    }

    private static string SafeResult(Task<string> task)
    {
        try
        {
            return task.Wait(TimeSpan.FromSeconds(5)) ? task.Result : string.Empty;
        }
        catch (AggregateException)
        {
            return string.Empty;
        }
    }
}

internal static class MacNative
{
    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);

    public static uint CurrentUserId()
    {
        try
        {
            return GetUid();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            var id = MacProcessRunner.Run("/usr/bin/id", ["-u"], TimeSpan.FromSeconds(10));
            if (id.Succeeded && uint.TryParse(id.StandardOutput.Trim(), out var uid))
            {
                return uid;
            }

            throw new InvalidOperationException("无法确定当前 macOS 用户。");
        }
    }

    /// <summary>Physical path of an existing directory (symlinks resolved), or the input when unresolvable.</summary>
    public static string ResolvePhysicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            return full;
        }

        IntPtr pointer;
        try
        {
            pointer = RealPath(full, IntPtr.Zero);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return full;
        }

        if (pointer == IntPtr.Zero)
        {
            return full;
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer) ?? full;
        }
        finally
        {
            Free(pointer);
        }
    }
}
