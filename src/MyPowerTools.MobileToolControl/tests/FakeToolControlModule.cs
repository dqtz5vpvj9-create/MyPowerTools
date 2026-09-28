using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;

namespace MyPowerTools.MobileToolControl.Tests;

/// <summary>
/// Contract fake for the <c>mobile-tool-control</c> module.
///
/// It returns the same JSON documents the real module returns (device list, import preview/confirm,
/// catalog, invocation, cancel) and records every call with its arguments, so a test can assert the
/// exact payload the page sent and which module command it used. No socket, no secret store and no
/// device is involved.
/// </summary>
internal sealed class FakeToolControlModule
{
    public List<(string CommandId, JsonObject Args)> Calls { get; } = [];

    /// <summary>Starts with one imported computer, the state the page sees after a real import.</summary>
    public List<JsonObject> Devices { get; } =
    [
        new JsonObject
        {
            ["deviceId"] = "grant-1",
            ["deviceName"] = "工作电脑",
            ["endpoint"] = "http://100.64.0.2:49541",
            ["platform"] = "windows",
            ["importedAt"] = "2026-01-01T00:00:00+00:00",
            ["lastState"] = "imported",
            ["lastDetail"] = "尚未检查",
            ["lastCheckedAt"] = "",
            ["credentialConfigured"] = true
        }
    ];

    /// <summary>Result of <c>import.preview</c>: ok plus the parsed fields.</summary>
    public JsonObject ImportPreview { get; set; } = new()
    {
        ["ok"] = true,
        ["error"] = "",
        ["version"] = 1,
        ["endpoint"] = "http://100.64.0.2:49541",
        ["endpointDisplay"] = "100.64.0.2:49541",
        ["grantId"] = "grant-1",
        ["deviceName"] = "工作电脑",
        ["tokenConfigured"] = true,
        ["alreadyImported"] = false,
        ["replacesExisting"] = false,
        ["addressRule"] = "100.64.0.0/10"
    };

    /// <summary>Catalog document returned for <c>catalog</c>.</summary>
    public JsonObject Catalog { get; set; } = new()
    {
        ["device"] = new JsonObject { ["name"] = "工作电脑", ["platform"] = "windows" },
        ["tools"] = new JsonArray(),
        ["commands"] = new JsonArray(),
        ["toolCount"] = 0,
        ["commandCount"] = 0,
        ["fromCache"] = false
    };

    /// <summary>States returned by successive <c>invocation.status</c> calls (last one repeats).</summary>
    public Queue<JsonObject> PollResponses { get; } = new();

    /// <summary>When set, a status read waits on it; used to test single-flight polling and stale answers.</summary>
    public TaskCompletionSource? StatusGate { get; set; }

    /// <summary>The invocation returned by <c>invoke</c>.</summary>
    public JsonObject Invocation { get; set; } = new()
    {
        ["invocationId"] = "inv-1",
        ["commandId"] = "input-monitor.stats",
        ["state"] = "running",
        ["message"] = "已提交",
        ["terminal"] = false,
        ["cancelAccepted"] = false,
        ["result"] = null
    };

    /// <summary>What <c>invoke.cancel</c> returns; the page must read this verbatim.</summary>
    public JsonObject CancelAnswer { get; set; } = new()
    {
        ["invocationId"] = "inv-1",
        ["commandId"] = "paste-image.upload",
        ["state"] = "cancelling",
        ["message"] = "已向运行时请求取消。",
        ["terminal"] = false,
        ["accepted"] = true,
        ["cancelAccepted"] = true,
        ["result"] = null
    };

    /// <summary>When set, the named command fails with this code and message.</summary>
    public Dictionary<string, MptRuntimeError> Failures { get; } = new(StringComparer.Ordinal);

    public JsonObject? LastArgs(string commandId) =>
        Calls.LastOrDefault(call => call.CommandId == commandId).Args;

    public int CountOf(string commandId) => Calls.Count(call => call.CommandId == commandId);

    public async Task<CommandExecutionResult> ExecuteAsync(
        string commandId,
        JsonObject? args,
        CancellationToken cancellationToken)
    {
        Calls.Add((commandId, args?.DeepClone().AsObject() ?? new JsonObject()));
        if (Failures.TryGetValue(commandId, out var failure))
        {
            return new CommandExecutionResult(
                "test-invocation",
                commandId,
                "failed",
                false,
                "",
                failure);
        }

        var output = commandId switch
        {
            MobileToolControlContract.CommandDevicesList => DevicesPayload(),
            MobileToolControlContract.CommandStatus => DevicesPayload(),
            MobileToolControlContract.CommandImportPreview => ImportPreview.ToJsonString(),
            MobileToolControlContract.CommandImportConfirm => ConfirmImport(args),
            MobileToolControlContract.CommandDevicesRemove => RemoveDevice(args),
            MobileToolControlContract.CommandDevicesCheck => new JsonObject
            {
                ["deviceId"] = args?["deviceId"]?.GetValue<string>() ?? "",
                ["reachable"] = true,
                ["deviceName"] = "工作电脑",
                ["platform"] = "windows",
                ["toolCount"] = 1,
                ["commandCount"] = Catalog["commands"]?.AsArray().Count ?? 0,
                ["detail"] = "电脑已响应，可以读取工具目录。"
            }.ToJsonString(),
            MobileToolControlContract.CommandCatalog => Catalog.ToJsonString(),
            MobileToolControlContract.CommandInvoke => Invocation.ToJsonString(),
            MobileToolControlContract.CommandInvocationStatus => await NextPollAsync().ConfigureAwait(true),
            MobileToolControlContract.CommandInvokeCancel => CancelAnswer.ToJsonString(),
            _ => throw new InvalidOperationException($"Fake module does not implement '{commandId}'.")
        };

        return new CommandExecutionResult("test-invocation", commandId, "succeeded", true, output);
    }

    private string DevicesPayload() => new JsonObject
    {
        ["devices"] = new JsonArray(Devices.Select(device => (JsonNode)device.DeepClone()).ToArray()),
        ["count"] = Devices.Count,
        ["loadError"] = "",
        ["addressRule"] = "100.64.0.0/10"
    }.ToJsonString();

    private string ConfirmImport(JsonObject? args)
    {
        var device = new JsonObject
        {
            ["deviceId"] = ImportPreview["grantId"]!.GetValue<string>(),
            ["deviceName"] = ImportPreview["deviceName"]!.GetValue<string>(),
            ["endpoint"] = ImportPreview["endpoint"]!.GetValue<string>(),
            ["platform"] = "",
            ["importedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["lastState"] = "imported",
            ["lastDetail"] = "尚未检查",
            ["lastCheckedAt"] = "",
            ["credentialConfigured"] = true
        };
        Devices.RemoveAll(existing =>
            string.Equals(existing["deviceId"]!.GetValue<string>(), device["deviceId"]!.GetValue<string>(), StringComparison.Ordinal));
        Devices.Add(device);
        return DevicesPayload();
    }

    private string RemoveDevice(JsonObject? args)
    {
        var deviceId = args?["deviceId"]?.GetValue<string>() ?? "";
        Devices.RemoveAll(device => string.Equals(device["deviceId"]!.GetValue<string>(), deviceId, StringComparison.Ordinal));
        return DevicesPayload();
    }

    private async Task<string> NextPollAsync()
    {
        if (StatusGate is not null)
        {
            await StatusGate.Task.ConfigureAwait(true);
        }

        if (PollResponses.Count > 1)
        {
            return PollResponses.Dequeue().ToJsonString();
        }

        return PollResponses.Count == 1 ? PollResponses.Peek().ToJsonString() : Invocation.ToJsonString();
    }

    /// <summary>A catalog with the three priority tools plus one unrelated tool.</summary>
    public static JsonObject SampleCatalog(bool includeUnauthorized = true)
    {
        var commands = new JsonArray(
            Command("input-monitor.stats", "input-monitor", "查看使用统计", "统计今天的键鼠活动", true),
            Command("input-monitor.rest", "input-monitor", "开始休息", "在电脑上开始一次休息", true),
            Command("screenease.effect.status", "screenease", "查看当前显示方案", "亮度与色温状态", true),
            Command("screenease.effect.toggle", "screenease", "切换护眼", "开启或关闭护眼", true),
            Command("paste-image.history", "paste-image", "图片历史", "电脑上传过的图片", true),
            Command("paste-image.upload", "paste-image", "上传图片", "把手机图片送到电脑剪贴板", true, parameters: true),
            Command("nssm-manager.service.restart", "nssm-manager", "重启服务", "需要管理员权限", !(includeUnauthorized)),
            Command("lorem.tool.magic", "lorem", "未知工具动作", "电脑自己声明的说明", true));

        return new JsonObject
        {
            ["device"] = new JsonObject { ["name"] = "工作电脑", ["platform"] = "windows" },
            ["tools"] = new JsonArray(
                Tool("input-monitor", "输入监测", "查看使用习惯", "Wellbeing"),
                Tool("screenease", "屏幕舒适", "让屏幕适合此刻的光线", "Display"),
                Tool("paste-image", "图片快传", "上传剪贴板图片", "Clipboard"),
                Tool("nssm-manager", "服务管理", "查看和管理 Windows 服务", "System"),
                Tool("lorem", "别的工具", "电脑上的其他工具", "Other")),
            ["commands"] = commands,
            ["toolCount"] = 5,
            ["commandCount"] = commands.Count,
            ["fetchedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["fromCache"] = false
        };
    }

    private static JsonObject Command(
        string commandId,
        string moduleId,
        string title,
        string subtitle,
        bool allowed,
        bool parameters = false)
    {
        var parameterArray = new JsonArray();
        if (parameters)
        {
            parameterArray.Add(new JsonObject
            {
                ["id"] = "path",
                ["label"] = "图片路径",
                ["type"] = "string",
                ["required"] = true,
                ["defaultValue"] = ""
            });
            parameterArray.Add(new JsonObject
            {
                ["id"] = "width",
                ["label"] = "宽度",
                ["type"] = "int",
                ["required"] = false,
                ["defaultValue"] = "1024"
            });
            parameterArray.Add(new JsonObject
            {
                ["id"] = "reuse",
                ["label"] = "复用远端路径",
                ["type"] = "bool",
                ["required"] = false,
                ["defaultValue"] = "true"
            });
        }

        return new JsonObject
        {
            ["commandId"] = commandId,
            ["moduleId"] = moduleId,
            ["title"] = title,
            ["subtitle"] = subtitle,
            ["dangerLevel"] = "",
            ["requiresElevation"] = commandId.StartsWith("nssm", StringComparison.Ordinal),
            ["supportsProgress"] = false,
            ["supportsCancellation"] = true,
            ["allowed"] = allowed,
            ["notAllowedReason"] = allowed ? "" : "此电脑未允许需要管理员权限的命令。",
            ["parameters"] = parameterArray
        };
    }

    private static JsonObject Tool(string toolId, string title, string description, string category) => new()
    {
        ["toolId"] = toolId,
        ["moduleId"] = toolId,
        ["title"] = title,
        ["description"] = description,
        ["category"] = category,
        ["state"] = "ready",
        ["availability"] = "available"
    };
}
