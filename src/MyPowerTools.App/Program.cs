using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.App;

internal static class Program
{
    private const string ShellActivationPipeName = "MyPowerTools.ShellActivation";
    private const int ActivationConnectTimeoutMilliseconds = 40;
    private const int ActivationAcknowledgementTimeoutMilliseconds = 250;
    private const byte ActivationAcknowledged = 0x06;
    private static ReadOnlySpan<byte> FocusShellPayload =>
        "{\"ToolActivation\":null,\"ShowShell\":true}"u8;

    [STAThread]
    public static int Main(string[] args)
    {
        var launchArguments = NormalizeActivationArguments(args);
        var toolActivation = ToolActivationProtocol.Parse(launchArguments);
        if (CanUseFastActivation(launchArguments, toolActivation) &&
            TryActivateRunningShell(toolActivation))
        {
            return 0;
        }

        var root = FindApplicationRoot(AppContext.BaseDirectory);
        DotNetRuntimeEnvironment.ConfigureCurrentProcess(root);
        var shell = ResolveShellExecutable(root);
        if (!File.Exists(shell))
        {
            ShowStartupError(IncompletePackageMessage());
            return 2;
        }

        TryStartServiceManager(root, ResolveDataRoot(launchArguments));

        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            WorkingDirectory = root,
            UseShellExecute = false
        };

        PrependAndroidPlatformToolsToPath(startInfo, root);
        DotNetRuntimeEnvironment.ConfigureChildProcess(startInfo, root);
        AddDefaultShellArguments(startInfo, root, launchArguments);
        var shellProcess = Process.Start(startInfo);
        if (shellProcess is not null)
        {
            TransferForegroundPermission(shellProcess.Id);
        }
        return 0;
    }

    private static bool CanUseFastActivation(
        IReadOnlyList<string> launchArguments,
        ToolActivationRequest? toolActivation)
    {
        for (var index = 0; index < launchArguments.Count; index++)
        {
            if (string.Equals(launchArguments[index], "--modules", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(launchArguments[index], "--data-root", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= launchArguments.Count)
                {
                    return false;
                }

                continue;
            }

            if (toolActivation is not null &&
                string.Equals(
                    launchArguments[index],
                    ToolActivationProtocol.ArgumentName,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= launchArguments.Count)
                {
                    return false;
                }

                continue;
            }

            if (toolActivation is not null &&
                launchArguments[index].StartsWith(
                    ToolActivationProtocol.ArgumentName + "=",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static IReadOnlyList<string> NormalizeActivationArguments(IReadOnlyList<string> args)
    {
        if (ToolActivationProtocol.Parse(args) is not null)
        {
            return args;
        }

        for (var index = 0; index < args.Count; index++)
        {
            var activation = ToolActivationProtocol.ParseProductActivationUri(args[index]);
            if (activation is null)
            {
                continue;
            }

            var normalized = args.Where((_, itemIndex) => itemIndex != index).ToList();
            normalized.Add(ToolActivationProtocol.ArgumentName);
            normalized.Add(ToolActivationProtocol.Serialize(activation));
            return normalized;
        }
        return args;
    }

    /// <summary>
    /// Hands the activation to a Shell that is already running. The Shell acknowledges on
    /// receipt, before it presents its window, so this stays a millisecond-scale handshake and a
    /// missing acknowledgement means nobody took the request.
    /// </summary>
    private static bool TryActivateRunningShell(ToolActivationRequest? toolActivation)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                ShellActivationPipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            pipe.Connect(ActivationConnectTimeoutMilliseconds);
            TransferForegroundPermission(pipe);

            var ownedPayload = toolActivation is null
                ? null
                : CreateToolActivationPayload(toolActivation);
            ReadOnlySpan<byte> payload = ownedPayload is null
                ? FocusShellPayload
                : ownedPayload;
            Span<byte> header = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            pipe.Write(header);
            pipe.Write(payload);
            pipe.Flush();

            using var responseTimeout = new CancellationTokenSource(
                ActivationAcknowledgementTimeoutMilliseconds);
            var response = new byte[1];
            var read = pipe.ReadAsync(response, responseTimeout.Token)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (read == 1 && response[0] == ActivationAcknowledged)
            {
                return true;
            }

            // The write reached the pipe but nothing acknowledged it, so no Shell owns this
            // request. Start a new instance rather than dropping the launch on the floor.
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    private static byte[] CreateToolActivationPayload(ToolActivationRequest activation)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WritePropertyName("ToolActivation");
        writer.WriteRawValue(ToolActivationProtocol.Serialize(activation));
        writer.WriteBoolean("ShowShell", !activation.SuppressShellWindow);
        writer.WriteBoolean("ShutdownShell", false);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void TransferForegroundPermission(NamedPipeClientStream pipe)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var shellProcessId) &&
            shellProcessId != 0)
        {
            _ = AllowSetForegroundWindow(shellProcessId);
        }
    }

    private static void TransferForegroundPermission(int processId)
    {
        if (OperatingSystem.IsWindows() && processId > 0)
        {
            _ = AllowSetForegroundWindow((uint)processId);
        }
    }

    private static void AddDefaultShellArguments(ProcessStartInfo startInfo, string root, IReadOnlyList<string> args)
    {
        if (!HasOption(args, "--modules"))
        {
            startInfo.ArgumentList.Add("--modules");
            startInfo.ArgumentList.Add(Path.Combine(root, "modules"));
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
    }

    private static string ResolveDataRoot(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], "--data-root", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools");
    }

    private static void TryStartServiceManager(string root, string dataRoot)
    {
        var executable = ResolveServiceManagerExecutable(root);
        var deployRoot = Path.Combine(root, "ServiceUnits");
        if (!File.Exists(executable) || !Directory.Exists(Path.Combine(deployRoot, "units")))
        {
            return;
        }
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--data-root");
            startInfo.ArgumentList.Add(dataRoot);
            startInfo.ArgumentList.Add("--deploy-root");
            startInfo.ArgumentList.Add(deployRoot);
            DotNetRuntimeEnvironment.ConfigureChildProcess(startInfo, root);
            Process.Start(startInfo);
        }
        catch
        {
            // Shell startup remains available while the Services page reports manager diagnostics.
        }
    }

    private static bool HasOption(IReadOnlyList<string> args, string option)
    {
        return args.Any(arg => string.Equals(arg, option, StringComparison.OrdinalIgnoreCase));
    }

    private static void PrependAndroidPlatformToolsToPath(ProcessStartInfo startInfo, string root)
    {
        var platformTools = Path.Combine(root, "Tools", "AndroidPlatformTools");
        if (!Directory.Exists(platformTools))
        {
            return;
        }

        startInfo.Environment.TryGetValue("PATH", out var inheritedPath);
        startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(inheritedPath)
            ? platformTools
            : platformTools + Path.PathSeparator + inheritedPath;
    }

    private static string FindApplicationRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(ResolveShellExecutable(directory.FullName)) &&
                Directory.Exists(Path.Combine(directory.FullName, "modules")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static string IncompletePackageMessage()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "MyPowerTools 应用包不完整，请重新安装 MyPowerTools.app。\n\n" +
                "The MyPowerTools application bundle is incomplete. Please reinstall MyPowerTools.app.";
        }

        return "MyPowerTools 包不完整，请重新解压完整的 MyPowerTools-win-x64.zip。\n\n" +
            "The MyPowerTools package is incomplete. Please re-extract the full zip.";
    }

    /// <summary>
    /// Reports a launcher failure the user can act on. The launcher has no UI toolkit loaded,
    /// so each platform gets the dialog its own shell provides and stderr always carries the
    /// text for log capture.
    /// </summary>
    private static void ShowStartupError(string message)
    {
        Console.Error.WriteLine(message);
        if (OperatingSystem.IsWindows())
        {
            MessageBoxW(IntPtr.Zero, message, "MyPowerTools", 0x00000010);
            return;
        }

        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(
                $"display alert {EscapeAppleScriptString("MyPowerTools")} " +
                $"message {EscapeAppleScriptString(message)} as critical");
            Process.Start(startInfo)?.WaitForExit(5000);
        }
        catch (Exception exception) when (exception is SystemException or InvalidOperationException)
        {
            // stderr already carries the message; a missing osascript must not replace the
            // launcher's exit code with an unhandled exception.
        }
    }

    private static string EscapeAppleScriptString(string value)
    {
        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }

    private static string ExecutableName(string baseName) =>
        OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;

    /// <summary>
    /// Resolves a sibling host executable across both application layouts. The macOS bundle
    /// ships each host as a nested helper bundle under Contents/MacOS/Helpers, because an
    /// executable only gets an NSBundle identity when it sits in
    /// &lt;bundle&gt;.app/Contents/MacOS. Windows and the repository layout keep the flat
    /// &lt;root&gt;/&lt;host&gt;/&lt;executable&gt; form, which the macOS bundle also carries as
    /// compatibility links, so the nested bundle is preferred and the flat path answers
    /// everywhere else.
    /// </summary>
    private static string ResolveHostExecutable(
        string root,
        string hostDirectory,
        string helperBundle,
        string baseName)
    {
        if (OperatingSystem.IsMacOS())
        {
            var nested = Path.Combine(root, "Helpers", helperBundle, "Contents", "MacOS", baseName);
            if (File.Exists(nested))
            {
                return nested;
            }
        }

        return Path.Combine(root, hostDirectory, ExecutableName(baseName));
    }

    private static string ResolveShellExecutable(string root) =>
        ResolveHostExecutable(root, "Shell", "MyPowerTools Shell.app", "MyPowerTools.Shell.Avalonia");

    private static string ResolveServiceManagerExecutable(string root) =>
        ResolveHostExecutable(root, "ServiceManager", "MyPowerTools ServiceManager.app", "MyPowerTools.ServiceManager");

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
