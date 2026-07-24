using System.Diagnostics;
using System.Security;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Mac;

public sealed class MacLaunchdAutostartService : IAutostartService
{
    public async Task<ServiceStatus> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new ServiceStatus(id, "unsupported", "launchd autostart is available only on macOS.");
        }

        var label = LaunchdSupport.AutostartLabel(id);
        var result = await LaunchdSupport.RunAsync("print", LaunchdSupport.ServiceTarget(label), cancellationToken);
        return new ServiceStatus(
            id,
            result.ExitCode == 0 ? "enabled" : "disabled",
            result.ExitCode == 0 ? result.Output.Trim() : result.Error.Trim());
    }

    public async Task<BrokerOperationResult> EnableAsync(
        string id,
        string command,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new BrokerOperationResult(false, "unsupported", "launchd autostart is available only on macOS.");
        }

        var label = LaunchdSupport.AutostartLabel(id);
        var path = LaunchdSupport.AgentPath(label);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plist = LaunchdSupport.CreateCommandPlist(label, command, runAtLoad: true);
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, plist, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);

        _ = await LaunchdSupport.RunAsync("bootout", LaunchdSupport.ServiceTarget(label), cancellationToken);
        var result = await LaunchdSupport.RunAsync("bootstrap", LaunchdSupport.Domain, path, cancellationToken);
        return result.ExitCode == 0
            ? new BrokerOperationResult(true, "enabled", $"launchd agent '{label}' enabled.")
            : new BrokerOperationResult(false, "failed", LaunchdSupport.Message(result));
    }

    public async Task<BrokerOperationResult> DisableAsync(string id, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new BrokerOperationResult(false, "unsupported", "launchd autostart is available only on macOS.");
        }

        var label = LaunchdSupport.AutostartLabel(id);
        var result = await LaunchdSupport.RunAsync("bootout", LaunchdSupport.ServiceTarget(label), cancellationToken);
        var path = LaunchdSupport.AgentPath(label);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return result.ExitCode is 0 or 3
            ? new BrokerOperationResult(true, "disabled", $"launchd agent '{label}' disabled.")
            : new BrokerOperationResult(false, "failed", LaunchdSupport.Message(result));
    }
}

public sealed class MacLaunchdServiceManager : IServiceManager
{
    public async Task<ServiceStatus> GetStatusAsync(string serviceName, CancellationToken cancellationToken)
    {
        LaunchdSupport.ValidateLabel(serviceName);
        if (!OperatingSystem.IsMacOS())
        {
            return new ServiceStatus(serviceName, "unsupported", "launchd services are available only on macOS.");
        }

        var result = await LaunchdSupport.RunAsync("print", LaunchdSupport.ServiceTarget(serviceName), cancellationToken);
        var state = result.ExitCode != 0
            ? "missing"
            : result.Output.Contains("state = running", StringComparison.OrdinalIgnoreCase)
                ? "running"
                : "loaded";
        return new ServiceStatus(serviceName, state, LaunchdSupport.Message(result));
    }

    public async Task<BrokerOperationResult> StartAsync(string serviceName, CancellationToken cancellationToken)
    {
        LaunchdSupport.ValidateLabel(serviceName);
        if (!OperatingSystem.IsMacOS())
        {
            return new BrokerOperationResult(false, "unsupported", "launchd services are available only on macOS.");
        }

        var result = await LaunchdSupport.RunAsync("kickstart", "-k", LaunchdSupport.ServiceTarget(serviceName), cancellationToken);
        return LaunchdSupport.OperationResult(result, "started", serviceName);
    }

    public async Task<BrokerOperationResult> StopAsync(string serviceName, CancellationToken cancellationToken)
    {
        LaunchdSupport.ValidateLabel(serviceName);
        if (!OperatingSystem.IsMacOS())
        {
            return new BrokerOperationResult(false, "unsupported", "launchd services are available only on macOS.");
        }

        var result = await LaunchdSupport.RunAsync("kill", "SIGTERM", LaunchdSupport.ServiceTarget(serviceName), cancellationToken);
        return LaunchdSupport.OperationResult(result, "stopped", serviceName);
    }
}

internal static class LaunchdSupport
{
    private static readonly Lazy<int> UserId = new(ResolveUserId);

    public static string Domain => $"gui/{UserId.Value}";

    public static string ServiceTarget(string label) => $"{Domain}/{label}";

    public static string AutostartLabel(string id)
    {
        ValidateLabel(id);
        return $"com.mypowertools.autostart.{id}";
    }

    public static string AgentPath(string label)
    {
        ValidateLabel(label);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "LaunchAgents",
            label + ".plist");
    }

    public static void ValidateLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 180 ||
            value.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException("launchd labels may contain letters, digits, dot, dash, and underscore.", nameof(value));
        }
    }

    public static string CreateCommandPlist(string label, string command, bool runAtLoad)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var escapedLabel = SecurityElement.Escape(label);
        var escapedCommand = SecurityElement.Escape(command);
        return $"""
               <?xml version="1.0" encoding="UTF-8"?>
               <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
               <plist version="1.0">
               <dict>
                 <key>Label</key><string>{escapedLabel}</string>
                 <key>ProgramArguments</key>
                 <array><string>/bin/zsh</string><string>-lc</string><string>{escapedCommand}</string></array>
                 <key>RunAtLoad</key><{runAtLoad.ToString().ToLowerInvariant()}/>
                 <key>ProcessType</key><string>Interactive</string>
               </dict>
               </plist>
               """;
    }

    public static async Task<LaunchdResult> RunAsync(params object[] values)
    {
        var cancellationToken = values[^1] is CancellationToken token
            ? token
            : CancellationToken.None;
        var arguments = values.Take(values.Length - 1).Select(value => value?.ToString() ?? "").ToArray();
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/launchctl",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start launchctl.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new LaunchdResult(process.ExitCode, await outputTask, await errorTask);
    }

    public static string Message(LaunchdResult result)
    {
        var message = string.Join(" ", new[] { result.Output.Trim(), result.Error.Trim() }
            .Where(value => value.Length > 0));
        return message.Length > 0 ? message : $"launchctl exited with code {result.ExitCode}.";
    }

    public static BrokerOperationResult OperationResult(LaunchdResult result, string state, string label)
    {
        return result.ExitCode == 0
            ? new BrokerOperationResult(true, state, $"launchd service '{label}' {state}.")
            : new BrokerOperationResult(false, "failed", Message(result));
    }

    private static int ResolveUserId()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/id",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-u");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start id.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !int.TryParse(output.Trim(), out var userId))
        {
            throw new InvalidOperationException("Could not determine the macOS user id.");
        }
        return userId;
    }

    internal sealed record LaunchdResult(int ExitCode, string Output, string Error);
}
