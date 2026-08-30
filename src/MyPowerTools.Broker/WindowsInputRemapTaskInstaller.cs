using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MyPowerTools.Broker;

/// <summary>
/// Installs the small interactive input remapper in a protected location and keeps its
/// logon task separate from the general-purpose ElevatedBroker. The task is created by an
/// elevated process once, then Task Scheduler starts it at the user's interactive logon.
/// </summary>
public static class WindowsInputRemapTaskInstaller
{
    public const string TaskName = @"\MyPowerTools WinSpace Shift";
    public const string HostFileName = "MyPowerTools.InputRemapHost.exe";

    public static string GetProtectedHostPath()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            throw new InvalidOperationException("Windows Program Files directory is unavailable.");
        }

        return Path.Combine(programFiles, "MyPowerTools", "InputRemap", HostFileName);
    }

    public static string GetStatePath(string dataRoot)
    {
        return Path.Combine(
            Path.GetFullPath(dataRoot),
            "state",
            "tools",
            "ime-manager",
            "win-space-shift-task.json");
    }

    public static InputRemapTaskState? ReadState(string dataRoot)
    {
        try
        {
            var path = GetStatePath(dataRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<InputRemapTaskState>(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static bool IsInstalledForSource(string dataRoot, string sourcePath)
    {
        try
        {
            var source = new FileInfo(Path.GetFullPath(sourcePath));
            var state = ReadState(dataRoot);
            return source.Exists &&
                   state is not null &&
                   state.SourceLength == source.Length &&
                   state.SourceWriteTimeUtcTicks == source.LastWriteTimeUtc.Ticks &&
                   File.Exists(GetProtectedHostPath());
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static int Install(string dataRoot, string sourcePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var diagnosticLogPath = GetDiagnosticLogPath(dataRoot);
        WriteDiagnostic(diagnosticLogPath, $"Install requested. source={sourcePath}; dataRoot={dataRoot}");
        var source = new FileInfo(Path.GetFullPath(sourcePath));
        if (!source.Exists || !string.Equals(source.Name, HostFileName, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Input remap host source is missing or invalid: {source.FullName}");
            WriteDiagnostic(diagnosticLogPath, $"Source validation failed: {source.FullName}");
            return 2;
        }

        var protectedHost = GetProtectedHostPath();
        var protectedDirectory = Path.GetDirectoryName(protectedHost)!;
        Directory.CreateDirectory(protectedDirectory);

        // Stop an earlier instance before replacing its image. A task restart is safe because
        // the host reads the user-level enabled flag before installing any mapping.
        RunSchtasks(["/End", "/TN", TaskName], diagnosticLogPath);
        try
        {
            CopyToProtectedLocation(source.FullName, protectedHost);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WriteDiagnostic(diagnosticLogPath, $"Protected host copy failed: {exception}");
            throw;
        }

        var currentSid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(currentSid))
        {
            throw new InvalidOperationException("The current Windows identity has no SID.");
        }

        var xmlPath = Path.Combine(protectedDirectory, $".{HostFileName}.{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(
                xmlPath,
                BuildTaskXml(currentSid, protectedHost, dataRoot),
                new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            if (!RunSchtasks(["/Create", "/TN", TaskName, "/XML", xmlPath, "/F"], diagnosticLogPath))
            {
                Console.Error.WriteLine("Could not register the MyPowerTools Win+Space input remap task.");
                WriteDiagnostic(diagnosticLogPath, "Task registration failed.");
                return 3;
            }
        }
        finally
        {
            TryDelete(xmlPath);
        }

        var sourceState = new FileInfo(source.FullName);
        var statePath = GetStatePath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var state = new InputRemapTaskState(
            TaskName,
            protectedHost,
            sourceState.Length,
            sourceState.LastWriteTimeUtc.Ticks,
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            statePath,
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (!RunSchtasks(["/Run", "/TN", TaskName], diagnosticLogPath))
        {
            Console.Error.WriteLine("The input remap task was registered, but could not be started immediately.");
            WriteDiagnostic(diagnosticLogPath, "Task start failed after registration.");
            return 4;
        }

        Console.WriteLine($"Registered protected input remap host: {protectedHost}");
        WriteDiagnostic(diagnosticLogPath, $"Install completed. protectedHost={protectedHost}");
        return 0;
    }

    public static int Uninstall(string dataRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var diagnosticLogPath = GetDiagnosticLogPath(dataRoot);
        WriteDiagnostic(diagnosticLogPath, $"Uninstall requested. dataRoot={dataRoot}");
        RunSchtasks(["/End", "/TN", TaskName], diagnosticLogPath);
        RunSchtasks(["/Delete", "/TN", TaskName, "/F"], diagnosticLogPath);
        TryDelete(GetProtectedHostPath());
        TryDelete(GetStatePath(dataRoot));
        WriteDiagnostic(diagnosticLogPath, "Uninstall completed.");
        return 0;
    }

    public static bool EnableTask() => RunSchtasks(["/Change", "/TN", TaskName, "/ENABLE"]);

    public static bool DisableTask()
    {
        RunSchtasks(["/End", "/TN", TaskName]);
        return RunSchtasks(["/Change", "/TN", TaskName, "/DISABLE"]);
    }

    public static bool RunTask() => RunSchtasks(["/Run", "/TN", TaskName]);

    private static void CopyToProtectedLocation(string sourcePath, string destinationPath)
    {
        var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: true);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                try
                {
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                    return;
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(100);
                }
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static string BuildTaskXml(string userSid, string hostPath, string dataRoot)
    {
        var command = Escape(hostPath);
        var arguments = Escape($"--data-root {Quote(dataRoot)}");
        var workingDirectory = Escape(Path.GetDirectoryName(hostPath)!);
        var escapedSid = Escape(userSid);

        return string.Join(
            Environment.NewLine,
            $"<?xml version=\"1.0\" encoding=\"UTF-16\"?>",
            "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">",
            "  <RegistrationInfo>",
            "    <Author>MyPowerTools</Author>",
            "    <Description>MyPowerTools Win+Space to Shift input remapper</Description>",
            "  </RegistrationInfo>",
            "  <Triggers>",
            "    <LogonTrigger>",
            "      <Enabled>true</Enabled>",
            $"      <UserId>{escapedSid}</UserId>",
            "    </LogonTrigger>",
            "  </Triggers>",
            "  <Principals>",
            "    <Principal id=\"Author\">",
            $"      <UserId>{escapedSid}</UserId>",
            "      <LogonType>InteractiveToken</LogonType>",
            "      <RunLevel>HighestAvailable</RunLevel>",
            "    </Principal>",
            "  </Principals>",
            "  <Settings>",
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>",
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>",
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>",
            "    <AllowHardTerminate>true</AllowHardTerminate>",
            "    <StartWhenAvailable>true</StartWhenAvailable>",
            "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>",
            "    <Enabled>true</Enabled>",
            "    <Hidden>true</Hidden>",
            "    <RestartOnFailure>",
            "      <Interval>PT1M</Interval>",
            "      <Count>3</Count>",
            "    </RestartOnFailure>",
            "  </Settings>",
            "  <Actions Context=\"Author\">",
            "    <Exec>",
            $"      <Command>{command}</Command>",
            $"      <Arguments>{arguments}</Arguments>",
            $"      <WorkingDirectory>{workingDirectory}</WorkingDirectory>",
            "    </Exec>",
            "  </Actions>",
            "</Task>");
    }

    private static bool RunSchtasks(IReadOnlyList<string> arguments, string? diagnosticLogPath = null)
    {
        var executable = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(15_000);
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            if (process.ExitCode != 0)
            {
                var detail = (process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd()).Trim();
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    Console.WriteLine($"schtasks.exe failed ({process.ExitCode}): {detail}");
                    WriteDiagnostic(diagnosticLogPath, $"schtasks failed ({process.ExitCode}) [{string.Join(" ", arguments)}]: {detail}");
                }
            }

            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is IOException or Win32Exception)
        {
            Console.WriteLine($"Could not run schtasks.exe: {exception.Message}");
            WriteDiagnostic(diagnosticLogPath, $"Could not run schtasks.exe [{string.Join(" ", arguments)}]: {exception}");
            return false;
        }
    }

    private static string GetDiagnosticLogPath(string dataRoot)
    {
        return Path.Combine(Path.GetFullPath(dataRoot), "logs", "input-remap-task-installer.log");
    }

    private static void WriteDiagnostic(string? path, string message)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(
                path,
                $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed record InputRemapTaskState(
    string TaskName,
    string ProtectedHostPath,
    long SourceLength,
    long SourceWriteTimeUtcTicks,
    DateTimeOffset InstalledAt);
