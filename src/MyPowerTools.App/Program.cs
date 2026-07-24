using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MyPowerTools.Abstractions;

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
        var shell = Path.Combine(root, "Shell", ExecutableName("MyPowerTools.Shell.Avalonia"));
        if (!File.Exists(shell))
        {
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

    private static bool TryActivateRunningShell(ToolActivationRequest? toolActivation)
    {
        var requestSent = false;
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
            requestSent = true;

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

            return requestSent;
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            return requestSent;
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
        var executable = Path.Combine(
            root,
            "ServiceManager",
            ExecutableName("MyPowerTools.ServiceManager"));
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
            if (File.Exists(Path.Combine(directory.FullName, "Shell", ExecutableName("MyPowerTools.Shell.Avalonia"))) &&
                Directory.Exists(Path.Combine(directory.FullName, "modules")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static string ExecutableName(string baseName) =>
        OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
