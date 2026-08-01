using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MyPowerTools.WebToolHost;

internal static class Program
{
    [STAThread]
    public static int Main(string[] arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 2;
        }
        if (arguments.Contains("--isolation-crash-probe", StringComparer.OrdinalIgnoreCase))
        {
            return IsolationProbe.Run(crashHost: true);
        }
        if (arguments.Contains("--isolation-probe", StringComparer.OrdinalIgnoreCase))
        {
            return IsolationProbe.Run(crashHost: false);
        }
        if (!TryReadLaunchOptions(
                arguments,
                out var parentWindow,
                out var parentProcessId,
                out var toolId,
                out var sourceUri,
                out var allowedOrigins))
        {
            return 2;
        }

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var host = WebSurfaceHostWindow.Create(
                parentWindow,
                parentProcessId,
                toolId,
                sourceUri,
                allowedOrigins);
            WebToolHostProtocol.WriteState("loading", phase: "attached");
            _ = Task.Run(() => RunCommandLoopAsync(host));
            _ = Task.Run(() => MonitorParentAsync(parentProcessId));
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            _ = host.InitializeAsync();
            Application.Run(new HostApplicationContext(host));
            return 0;
        }
        catch (Exception ex)
        {
            WebToolHostProtocol.WriteState(
                "failed",
                "WebToolHost 启动失败。Shell 仍可继续使用。",
                "host-start-failed");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task RunCommandLoopAsync(WebSurfaceHostWindow host)
    {
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                if (line.Length > WebToolHostProtocol.MaximumFrameLength)
                {
                    WebToolHostProtocol.WriteState("failed", "Shell command frame exceeded the WebToolHost protocol limit.", "protocol-frame-too-large");
                    return;
                }
                var command = WebToolHostProtocol.ParseCommand(line);
                if (command is null)
                {
                    continue;
                }
                _ = PostToHost(host, () => ApplyCommand(host, command));
                if (string.Equals(command.Type, "shutdown", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            _ = PostToHost(host, host.RequestClose);
        }
    }

    private static async Task MonitorParentAsync(uint parentProcessId)
    {
        try
        {
            using var parent = Process.GetProcessById(checked((int)parentProcessId));
            await parent.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        Environment.Exit(0);
    }

    private static bool PostToHost(Control host, Action action)
    {
        try
        {
            if (!host.IsDisposed && host.IsHandleCreated)
            {
                host.BeginInvoke(action);
                return true;
            }
        }
        catch (InvalidOperationException)
        {
        }
        return false;
    }

    private static void ApplyCommand(WebSurfaceHostWindow host, HostCommand command)
    {
        switch (command.Type.ToLowerInvariant())
        {
            case "bounds":
                host.ApplyBounds(command);
                break;
            case "reload":
                host.Reload();
                break;
            case "navigate":
                host.Navigate(command.Source);
                break;
            case "focus":
                host.FocusWebView(command.Direction);
                break;
            case "bridge-response":
                host.PostBridgeResponse(command.Payload);
                break;
            case "shutdown":
                host.RequestClose();
                break;
        }
    }

    private static bool TryReadLaunchOptions(
        IReadOnlyList<string> arguments,
        out nint parentWindow,
        out uint parentProcessId,
        out string toolId,
        out Uri sourceUri,
        out IReadOnlyList<Uri> allowedOrigins)
    {
        parentWindow = 0;
        parentProcessId = 0;
        toolId = "";
        sourceUri = new Uri(WebSurfaceHostWindow.ProbeSourceUrl);
        var originValues = new List<Uri>();
        var sourceProvided = false;
        var sourceValid = false;
        var allowedOriginsValid = true;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--parent-hwnd", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Count &&
                long.TryParse(arguments[++index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var handle))
            {
                parentWindow = (nint)handle;
            }
            else if (string.Equals(arguments[index], "--parent-pid", StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < arguments.Count &&
                     uint.TryParse(arguments[++index], NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
            {
                parentProcessId = processId;
            }
            else if (string.Equals(arguments[index], "--tool", StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < arguments.Count)
            {
                toolId = arguments[++index];
            }
            else if (string.Equals(arguments[index], "--source", StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < arguments.Count)
            {
                sourceProvided = true;
                sourceValid = Uri.TryCreate(arguments[++index], UriKind.Absolute, out var source) &&
                              WebSurfaceHostWindow.IsSupportedWebUri(source);
                if (sourceValid)
                {
                    sourceUri = source!;
                }
            }
            else if (string.Equals(arguments[index], "--allowed-origin", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= arguments.Count ||
                    !Uri.TryCreate(arguments[++index], UriKind.Absolute, out var origin) ||
                    !WebSurfaceHostWindow.IsSupportedWebUri(origin))
                {
                    allowedOriginsValid = false;
                }
                else
                {
                    originValues.Add(WebSurfaceHostWindow.NormalizeOrigin(origin));
                }
            }
        }
        allowedOrigins = originValues.Count == 0
            ? [WebSurfaceHostWindow.NormalizeOrigin(sourceUri)]
            : originValues.DistinctBy(value => value.AbsoluteUri, StringComparer.OrdinalIgnoreCase).ToArray();
        return parentWindow != 0 &&
               parentProcessId != 0 &&
               WebSurfaceHostWindow.IsSupportedToolId(toolId) &&
               sourceProvided &&
               sourceValid &&
               allowedOriginsValid &&
               WebSurfaceHostWindow.IsSupportedWebUri(sourceUri);
    }
}

internal sealed class HostApplicationContext : ApplicationContext
{
    public HostApplicationContext(Form host)
    {
        MainForm = host;
        host.FormClosed += (_, _) => ExitThread();
    }
}

internal static class IsolationProbe
{
    public static int Run(bool crashHost)
    {
        var parent = Win32Native.CreateWindowEx(
            0,
            "STATIC",
            "",
            Win32Native.WsOverlapped,
            0,
            0,
            320,
            240,
            0,
            0,
            Win32Native.GetModuleHandle(null),
            0);
        if (parent == 0)
        {
            return 3;
        }

        Process? process = null;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return 4;
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--tool");
            startInfo.ArgumentList.Add("smartbird");
            startInfo.ArgumentList.Add("--parent-hwnd");
            startInfo.ArgumentList.Add(parent.ToInt64().ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--source");
            startInfo.ArgumentList.Add(WebSurfaceHostWindow.ProbeSourceUrl);
            startInfo.ArgumentList.Add("--allowed-origin");
            startInfo.ArgumentList.Add(WebSurfaceHostWindow.ProbeSourceUrl);
            process = Process.Start(startInfo);
            if (process is null)
            {
                return 5;
            }

            process.StandardInput.WriteLine("{\"type\":\"bounds\",\"x\":0,\"y\":0,\"width\":320,\"height\":240,\"visible\":false}");
            process.StandardInput.Flush();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            var controllerReady = false;
            var lastPhase = "";
            var lastState = "";
            var lastMessage = "";
            var readTask = process.StandardOutput.ReadLineAsync();
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                if (!readTask.Wait(TimeSpan.FromSeconds(2)))
                {
                    continue;
                }
                var line = readTask.Result;
                if (line is null)
                {
                    break;
                }
                using var payload = JsonDocument.Parse(line);
                var phase = payload.RootElement.TryGetProperty("phase", out var phaseNode)
                    ? phaseNode.GetString()
                    : "";
                lastPhase = phase ?? "";
                lastState = payload.RootElement.TryGetProperty("state", out var stateNode)
                    ? stateNode.GetString() ?? ""
                    : "";
                lastMessage = payload.RootElement.TryGetProperty("message", out var messageNode)
                    ? messageNode.GetString() ?? ""
                    : "";
                if (string.Equals(phase, "controller-ready", StringComparison.Ordinal))
                {
                    controllerReady = true;
                    break;
                }
                if (string.Equals(phase, "runtime-missing", StringComparison.Ordinal) ||
                    string.Equals(phase, "initialization-failed", StringComparison.Ordinal) ||
                    string.Equals(phase, "host-start-failed", StringComparison.Ordinal))
                {
                    break;
                }
                readTask = process.StandardOutput.ReadLineAsync();
            }

            var childOwnedByHost = false;
            _ = Win32Native.EnumChildWindows(parent, (window, _) =>
            {
                Win32Native.GetWindowThreadProcessId(window, out var owner);
                if (owner == (uint)process.Id)
                {
                    childOwnedByHost = true;
                    return false;
                }
                return true;
            }, 0);

            if (crashHost)
            {
                process.Kill(entireProcessTree: true);
            }
            else
            {
                process.StandardInput.WriteLine("{\"type\":\"shutdown\"}");
                process.StandardInput.Flush();
            }
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
            }
            var standardError = process.StandardError.ReadToEnd();
            var parentSurvivedHostExit = Win32Native.IsWindow(parent);
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                ok = controllerReady && childOwnedByHost && parentSurvivedHostExit,
                controllerReady,
                crossProcessChild = childOwnedByHost,
                crashHost,
                parentSurvivedHostExit,
                hostExitCode = process.HasExited ? process.ExitCode : -1,
                lastState,
                lastPhase,
                lastMessage,
                standardError
            }));
            return controllerReady && childOwnedByHost && parentSurvivedHostExit ? 0 : 6;
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
            _ = Win32Native.DestroyWindow(parent);
        }
    }
}
