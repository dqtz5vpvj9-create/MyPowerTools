using System.Text.Json.Nodes;

namespace MyPowerTools.Shell.Avalonia.Services.Mobile;

/// <summary>One imported computer as the control module reports it. No token or endpoint is exposed.</summary>
public sealed record MobileControlDevice(
    string DeviceId,
    string Name,
    string Platform,
    string LastState,
    string LastDetail,
    bool CredentialConfigured)
{
    /// <summary>
    /// Honest connection wording. "已连接" is only shown after the computer itself answered a check;
    /// an imported record that was never checked stays "尚未检查".
    /// </summary>
    public string StateLabel => LastState switch
    {
        "reachable" => "已连接",
        "unreachable" => "无法连接",
        _ => "尚未检查"
    };

    public bool IsReachable => string.Equals(LastState, "reachable", StringComparison.Ordinal);
    public bool NeverChecked => !IsReachable && !string.Equals(LastState, "unreachable", StringComparison.Ordinal);
    public string PlatformLabel => Platform switch
    {
        "windows" => "Windows",
        "macos" or "darwin" => "macOS",
        "linux" => "Linux",
        _ => "电脑"
    };

    public string Subtitle => LastDetail.Length > 0 ? $"{PlatformLabel} · {LastDetail}" : PlatformLabel;
}

/// <summary>The imported computers plus the honest reason when the list could not be read.</summary>
public sealed record MobileControlDeviceSnapshot(IReadOnlyList<MobileControlDevice> Devices, string Error = "")
{
    public bool HasDevices => Devices.Count > 0;
    public string Notice => Error;
    public bool HasNotice => Error.Length > 0;
}

/// <summary>
/// Reads the imported computers from the real control module
/// (<c>mobile-tool-control.devices.list</c>). The device id here is the desktop grant id, never a
/// file-pairing device id.
/// </summary>
public interface IMobileControlDeviceService
{
    Task<MobileControlDeviceSnapshot> GetDevicesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation over the existing command execution boundary. Command answers decide the
/// result: a non-success state becomes an honest error, never an empty success.
/// </summary>
public sealed class MobileControlDeviceService : IMobileControlDeviceService
{
    /// <summary>The tool id the G2 module registers; its route id comes from the tool catalog.</summary>
    public const string ToolId = "mobile-tool-control";

    /// <summary>Fallback route id from the G2 integration contract when the catalog is not readable.</summary>
    public const string FallbackRouteId = "workspace";

    public const string DevicesListCommand = "mobile-tool-control.devices.list";

    private readonly Func<string, CancellationToken, Task<ShellCommandExecutionResult>> _execute;

    public MobileControlDeviceService()
        : this((commandId, cancellationToken) => new ShellCommandExecutionService().ExecuteAsync(commandId, null, cancellationToken))
    {
    }

    /// <summary>Injectable executor so tests never need a running module or host.</summary>
    public MobileControlDeviceService(Func<string, CancellationToken, Task<ShellCommandExecutionResult>> execute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public async Task<MobileControlDeviceSnapshot> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _execute(DevicesListCommand, cancellationToken).ConfigureAwait(true);
            var response = result.Response;
            if (!string.Equals(response.State, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return new MobileControlDeviceSnapshot([], FailureMessage(response));
            }

            return new MobileControlDeviceSnapshot(Parse(response.Summary));
        }
        catch (Exception ex)
        {
            return new MobileControlDeviceSnapshot([], ex.Message);
        }
    }

    public static IReadOnlyList<MobileControlDevice> Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        JsonObject? document;
        try
        {
            document = JsonNode.Parse(payload) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }

        if (document?["devices"] is not JsonArray array)
        {
            return [];
        }

        var devices = new List<MobileControlDevice>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject device)
            {
                continue;
            }

            var deviceId = Text(device, "deviceId");
            if (deviceId.Length == 0)
            {
                continue;
            }

            devices.Add(new MobileControlDevice(
                deviceId,
                Text(device, "deviceName"),
                Text(device, "platform"),
                Text(device, "lastState"),
                Text(device, "lastDetail"),
                Flag(device, "credentialConfigured")));
        }

        return devices;
    }

    /// <summary>The activation URI the control surface parses for a device (and optionally a tool).</summary>
    public static string BuildActivationUri(string deviceId, string? toolId = null)
    {
        var uri = $"mypowertools://device-tool?device={Uri.EscapeDataString(deviceId)}";
        return string.IsNullOrWhiteSpace(toolId)
            ? uri
            : $"{uri}&tool={Uri.EscapeDataString(toolId)}";
    }

    private static string FailureMessage(MyPowerTools.Protocol.HostControl.V1.CommandExecutionResponse response)
    {
        if (string.Equals(response.State, "permission-required", StringComparison.OrdinalIgnoreCase))
        {
            return "需要先在电脑上授权这台手机访问电脑工具。";
        }

        var message = string.IsNullOrWhiteSpace(response.ErrorMessage)
            ? response.Summary?.Trim()
            : response.ErrorMessage.Trim();
        return string.IsNullOrWhiteSpace(message)
            ? "电脑工具模块没有返回设备列表。"
            : message;
    }

    private static string Text(JsonObject document, string key) =>
        document[key]?.GetValue<string>()?.Trim() ?? "";

    private static bool Flag(JsonObject document, string key) =>
        document[key]?.GetValue<bool>() ?? false;
}
