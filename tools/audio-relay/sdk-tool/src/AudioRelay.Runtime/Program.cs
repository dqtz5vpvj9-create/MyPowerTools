using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using AudioRelay.MyPowerTools;
using Microsoft.Win32;

return AudioRelayRuntime.Run();

internal static class AudioRelayRuntime
{
    private const string DownloadsUrl = "https://audiorelay.net/downloads";
    private const string SendAudioGuideUrl = "https://audiorelay.net/docs/windows/stream-audio-from-your-pc-to-your-phone";
    private const string MicrophoneGuideUrl = "https://audiorelay.net/docs/windows/use-your-phone-as-a-mic-for-windows";
    private static readonly HashSet<string> Commands =
    [
        "audio-relay.health",
        "audio-relay.snapshot",
        "audio-relay.launch",
        "audio-relay.open-downloads",
        "audio-relay.open-send-audio-guide",
        "audio-relay.open-microphone-guide"
    ];

    public static int Run()
    {
        var requestId = "invalid";
        try
        {
            var line = Console.ReadLine() ?? throw new InvalidDataException("缺少 JSON-RPC 请求。");
            var root = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidDataException("JSON-RPC 请求必须为对象。");
            requestId = ReadString(root, "id");
            if (ReadString(root, "jsonrpc") != "2.0")
            {
                throw new InvalidDataException("jsonrpc 必须为 2.0。");
            }

            var commandId = ReadString(root, "commandId");
            if (!Commands.Contains(commandId))
            {
                throw new InvalidDataException($"未知命令 '{commandId}'。");
            }

            if (root["args"] is JsonObject arguments && arguments.Count != 0)
            {
                throw new InvalidDataException($"命令 '{commandId}' 不接受参数。");
            }

            var payload = Execute(commandId);
            WriteResponse(requestId, "ready", payload, null);
        }
        catch (Exception exception)
        {
            WriteResponse(requestId, "failed", null, exception.Message);
        }

        return 0;
    }

    private static JsonObject Execute(string commandId)
    {
        if (commandId is "audio-relay.health" or "audio-relay.snapshot")
        {
            return Snapshot();
        }

        if (commandId == "audio-relay.launch")
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("AudioRelay 桌面启动当前只支持 Windows。");
            }
            var installation = FindInstallation() ?? throw new InvalidOperationException("未找到 AudioRelay，请先从官网下载并安装桌面端。");
            Process.Start(new ProcessStartInfo(installation.ExecutablePath) { UseShellExecute = true });
            var snapshot = Snapshot();
            snapshot["launched"] = true;
            return snapshot;
        }

        var uri = commandId switch
        {
            "audio-relay.open-downloads" => DownloadsUrl,
            "audio-relay.open-send-audio-guide" => SendAudioGuideUrl,
            "audio-relay.open-microphone-guide" => MicrophoneGuideUrl,
            _ => throw new InvalidDataException($"未知命令 '{commandId}'。")
        };
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        return new JsonObject { ["openedUrl"] = uri };
    }

    private static JsonObject Snapshot()
    {
        var isWindows = OperatingSystem.IsWindows();
        var installation = OperatingSystem.IsWindows() ? FindInstallation() : null;
        var running = isWindows && Process.GetProcessesByName("AudioRelay").Length > 0;
        return new JsonObject
        {
            ["platform"] = isWindows ? "windows" : "unsupported",
            ["installed"] = installation is not null,
            ["running"] = running,
            ["version"] = installation?.Version,
            ["executablePath"] = installation?.ExecutablePath,
            ["downloadsUrl"] = DownloadsUrl,
            ["sendAudioGuideUrl"] = SendAudioGuideUrl,
            ["microphoneGuideUrl"] = MicrophoneGuideUrl
        };
    }

    [SupportedOSPlatform("windows")]
    private static AudioRelayInstallation? FindInstallation()
    {
        var registrations = ReadRegistrations().ToArray();
        var paths = KnownPaths();
        return AudioRelayInstallationLocator.Find(
            registrations,
            paths,
            File.Exists,
            path => FileVersionInfo.GetVersionInfo(path).ProductVersion);
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<AudioRelayProductRegistration> ReadRegistrations()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null)
            {
                continue;
            }

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var product = uninstall.OpenSubKey(name);
                var displayName = product?.GetValue("DisplayName") as string;
                if (displayName?.Contains("AudioRelay", StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }

                yield return new AudioRelayProductRegistration(
                    product?.GetValue("InstallLocation") as string,
                    product?.GetValue("DisplayIcon") as string,
                    product?.GetValue("DisplayVersion") as string);
            }
        }
    }

    private static IEnumerable<string> KnownPaths()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                 }.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            yield return Path.Combine(root, "AudioRelay", "AudioRelay.exe");
            yield return Path.Combine(root, "Programs", "AudioRelay", "AudioRelay.exe");
        }
    }

    private static string ReadString(JsonObject root, string name) =>
        root[name]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"{name} 必须为非空字符串。");

    private static void WriteResponse(string requestId, string state, JsonObject? payload, string? error)
    {
        var result = new JsonObject { ["state"] = state, ["payload"] = payload };
        if (error is not null)
        {
            result["error"] = new JsonObject { ["code"] = "audio-relay.failed", ["message"] = error };
        }

        Console.WriteLine(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
            ["result"] = result
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

}
