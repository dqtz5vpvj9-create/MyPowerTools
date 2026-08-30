using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImeManager.MyPowerTools;

return await ImeManagerRuntime.RunAsync();

internal static class ImeManagerRuntime
{
    private const string ToolId = "ime-manager";
    private const string WinSpaceShiftConfigFileName = "win-space-shift.json";
    private const int MaximumRequestCharacters = 64 * 1024;
    private const int MaximumIdentifierCharacters = 128;
    private static readonly HashSet<string> RequestProperties =
        ["jsonrpc", "id", "method", "commandId", "args"];
    private static readonly HashSet<string> Commands =
    [
        "ime-manager.health",
        "ime-manager.snapshot",
        "ime-manager.apply"
    ];
    private static readonly HashSet<string> SnapshotProperties = ["includeAllKeyboardLayouts"];
    private static readonly HashSet<string> ApplyProperties =
    [
        "enabledTipStrings",
        "defaultTipString",
        "languageHotkey",
        "layoutHotkey",
        "winSpaceMapsToShift",
        "includeAllKeyboardLayouts"
    ];

    public static Task<int> RunAsync()
    {
        var requestId = "invalid";
        var commandId = "";
        try
        {
            var line = ReadBoundedRequest(Console.In);
            var request = ParseRequest(line);
            requestId = request.RequestId;
            commandId = request.CommandId;
            ValidateArguments(commandId, request.Arguments);
            var payload = Execute(commandId, request.Arguments);
            WriteResponse(requestId, "ready", payload, errorCode: null, errorMessage: null);
        }
        catch (Exception exception)
        {
            var error = MapError(exception, commandId);
            WriteResponse(requestId, "failed", null, error.Code, error.Message);
        }

        return Task.FromResult(0);
    }

    private static string ReadBoundedRequest(TextReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n')
                {
                    return builder.ToString().TrimEnd('\r');
                }

                if (builder.Length >= MaximumRequestCharacters)
                {
                    throw new RuntimeProtocolException(
                        "request.invalid",
                        $"JSON-RPC 请求超过 {MaximumRequestCharacters} 个字符上限。");
                }

                builder.Append(character);
            }
        }

        if (builder.Length == 0)
        {
            throw new RuntimeProtocolException("request.invalid", "缺少 JSON-RPC 请求。");
        }

        return builder.ToString().TrimEnd('\r');
    }

    private static RuntimeRequest ParseRequest(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                line,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
        }
        catch (JsonException exception)
        {
            throw new RuntimeProtocolException(
                "request.invalid",
                $"JSON-RPC 请求无法解析：{exception.Message}",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new RuntimeProtocolException("request.invalid", "JSON-RPC 请求根节点必须为对象。");
            }

            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!observed.Add(property.Name))
                {
                    throw new RuntimeProtocolException(
                        "request.invalid",
                        $"JSON-RPC 请求包含重复字段 '{property.Name}'。");
                }

                if (!RequestProperties.Contains(property.Name))
                {
                    throw new RuntimeProtocolException(
                        "request.invalid",
                        $"JSON-RPC 请求包含未知字段 '{property.Name}'。");
                }
            }

            if (!TryReadBoundedString(root, "jsonrpc", 8, out var jsonrpc) ||
                jsonrpc != "2.0")
            {
                throw new RuntimeProtocolException("request.invalid", "jsonrpc 必须为 \"2.0\"。");
            }

            if (!TryReadBoundedString(root, "id", MaximumIdentifierCharacters, out var requestId) ||
                requestId.Length == 0)
            {
                throw new RuntimeProtocolException("request.invalid", "id 必须为非空字符串。");
            }

            if (root.TryGetProperty("method", out var methodElement) &&
                methodElement.ValueKind != JsonValueKind.String)
            {
                throw new RuntimeProtocolException("request.invalid", "method 必须为字符串。");
            }

            if (!TryReadBoundedString(root, "commandId", MaximumIdentifierCharacters, out var commandId) ||
                commandId.Length == 0)
            {
                throw new RuntimeProtocolException("request.invalid", "commandId 必须为非空字符串。");
            }

            JsonObject arguments;
            if (!root.TryGetProperty("args", out var argumentsElement) ||
                argumentsElement.ValueKind == JsonValueKind.Null ||
                argumentsElement.ValueKind == JsonValueKind.Undefined)
            {
                arguments = [];
            }
            else if (argumentsElement.ValueKind != JsonValueKind.Object)
            {
                throw new RuntimeProtocolException("request.invalid", "args 必须为对象。");
            }
            else
            {
                arguments = JsonNode.Parse(argumentsElement.GetRawText())?.AsObject() ??
                            throw new RuntimeProtocolException("request.invalid", "args 无法解析为 JSON 对象。");
            }

            return new RuntimeRequest(requestId, commandId, arguments);
        }
    }

    private static bool TryReadBoundedString(
        JsonElement root,
        string propertyName,
        int maximumLength,
        out string value)
    {
        value = "";
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? "";
        return value.Length <= maximumLength;
    }

    private static void ValidateArguments(string commandId, JsonObject arguments)
    {
        if (!Commands.Contains(commandId))
        {
            throw new RuntimeProtocolException("command.not-found", $"未知命令 '{commandId}'。");
        }

        var allowed = commandId switch
        {
            "ime-manager.snapshot" => SnapshotProperties,
            "ime-manager.apply" => ApplyProperties,
            _ => []
        };

        foreach (var property in arguments)
        {
            if (!allowed.Contains(property.Key))
            {
                throw new RuntimeProtocolException(
                    "validation.failed",
                    $"命令 '{commandId}' 包含未知参数 '{property.Key}'。");
            }
        }

        if (commandId == "ime-manager.health" && arguments.Count != 0)
        {
            throw new RuntimeProtocolException("validation.failed", "health 不接受参数。");
        }

        if (commandId is "ime-manager.snapshot" or "ime-manager.apply")
        {
            ValidateOptionalBoolean(arguments, "includeAllKeyboardLayouts");
        }

        if (commandId == "ime-manager.apply")
        {
            ValidateOptionalBoolean(arguments, "winSpaceMapsToShift");
        }

        if (commandId != "ime-manager.apply")
        {
            return;
        }

        if (arguments["enabledTipStrings"] is not JsonArray enabled ||
            enabled.Count == 0 ||
            enabled.Count > ParsedTipString.MaximumEnabledCount)
        {
            throw new RuntimeProtocolException(
                "validation.failed",
                $"enabledTipStrings 必须为 1 到 {ParsedTipString.MaximumEnabledCount} 项的数组。");
        }

        foreach (var item in enabled)
        {
            if (item is not JsonValue value ||
                !value.TryGetValue<string>(out var tip) ||
                !ParsedTipString.TryParse(tip, out _))
            {
                throw new RuntimeProtocolException(
                    "validation.failed",
                    "enabledTipStrings 只能包含有效的输入法标识。");
            }
        }

        if (arguments["defaultTipString"] is not JsonValue defaultValue ||
            !defaultValue.TryGetValue<string>(out var defaultTip) ||
            !ParsedTipString.TryParse(defaultTip, out _))
        {
            throw new RuntimeProtocolException("validation.failed", "defaultTipString 必须为有效的输入法标识。");
        }

        _ = ReadHotkey(arguments, "languageHotkey");
        _ = ReadHotkey(arguments, "layoutHotkey");
    }

    private static SwitchHotkey ReadHotkey(JsonObject arguments, string name)
    {
        try
        {
            var hotkey = arguments[name].Deserialize<SwitchHotkey>(ImeManagerJson.Compact);
            if (!Enum.IsDefined(hotkey))
            {
                throw new JsonException("枚举值超出范围。");
            }

            return hotkey;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentNullException)
        {
            throw new RuntimeProtocolException(
                "validation.failed",
                $"{name} 必须为 left-alt-shift、ctrl-shift、not-assigned 或 grave-accent。",
                exception);
        }
    }

    private static void ValidateOptionalBoolean(JsonObject arguments, string name)
    {
        if (arguments[name] is null)
        {
            return;
        }

        if (arguments[name] is not JsonValue value ||
            !value.TryGetValue<bool>(out _))
        {
            throw new RuntimeProtocolException("validation.failed", $"{name} 必须为布尔值。");
        }
    }

    private static JsonNode Execute(string commandId, JsonObject arguments)
    {
        if (commandId == "ime-manager.health")
        {
            return new JsonObject
            {
                ["toolId"] = ToolId,
                ["state"] = OperatingSystem.IsWindows() ? "ready" : "unsupported",
                ["runtimeVersion"] =
                    typeof(ImeManagerRuntime).Assembly.GetName().Version?.ToString() ?? "unknown",
                ["time"] = DateTimeOffset.UtcNow
            };
        }

        if (commandId == "ime-manager.snapshot")
        {
            if (!OperatingSystem.IsWindows())
            {
                return new JsonObject
                {
                    ["snapshot"] = JsonSerializer.SerializeToNode(
                        InputMethodSnapshot.Unsupported,
                        ImeManagerJson.Compact)
                };
            }

            #pragma warning disable CA1416
            return RunSta(() => Snapshot(arguments));
            #pragma warning restore CA1416
        }

        if (commandId == "ime-manager.apply")
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("输入法管理器仅支持 Windows。");
            }

            #pragma warning disable CA1416
            return RunSta(() => Apply(arguments));
            #pragma warning restore CA1416
        }

        throw new RuntimeProtocolException("command.not-found", $"未知命令 '{commandId}'。");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static JsonNode Snapshot(JsonObject arguments)
    {
        var catalog = new InputMethodCatalog(new WindowsInputMethodPlatform());
        var snapshot = catalog.Read(ReadOptions(arguments)) with
        {
            WinSpaceMapsToShift = ReadWinSpaceMapsToShift()
        };
        return new JsonObject
        {
            ["snapshot"] = JsonSerializer.SerializeToNode(snapshot, ImeManagerJson.Compact)
        };
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static JsonNode Apply(JsonObject arguments)
    {
        var enabled = arguments["enabledTipStrings"]!.AsArray()
            .Select(node => ParsedTipString.RequireCanonical(node!.GetValue<string>()))
            .ToArray();
        var plan = new InputMethodPlan(
            enabled,
            ParsedTipString.RequireCanonical(arguments["defaultTipString"]!.GetValue<string>()),
            new SwitchHotkeys(
                ReadHotkey(arguments, "languageHotkey"),
                ReadHotkey(arguments, "layoutHotkey")));
        var catalog = new InputMethodCatalog(new WindowsInputMethodPlatform());
        var result = catalog.Apply(plan, ReadOptions(arguments));
        var winSpaceMapsToShift = arguments["winSpaceMapsToShift"] is JsonValue value
            ? value.TryGetValue<bool>(out var requested) && requested
            : ReadWinSpaceMapsToShift();
        WriteWinSpaceMapsToShift(winSpaceMapsToShift);
        result = result with
        {
            Snapshot = result.Snapshot with
            {
                WinSpaceMapsToShift = winSpaceMapsToShift
            }
        };
        return JsonSerializer.SerializeToNode(result, ImeManagerJson.Compact)!;
    }

    private static bool ReadWinSpaceMapsToShift()
    {
        try
        {
            var path = WinSpaceShiftConfigPath();
            if (!File.Exists(path))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("enabled", out var enabled) &&
                   enabled.ValueKind == JsonValueKind.True;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void WriteWinSpaceMapsToShift(bool enabled)
    {
        var path = WinSpaceShiftConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        var document = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["enabled"] = enabled
        };
        File.WriteAllText(temporaryPath, document.ToJsonString(ImeManagerJson.Compact), Encoding.UTF8);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string WinSpaceShiftConfigPath()
    {
        var dataRoot = Environment.GetEnvironmentVariable("MPT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyPowerTools");
        }

        return Path.Combine(dataRoot, "state", "tools", ToolId, WinSpaceShiftConfigFileName);
    }

    private static InputMethodReadOptions ReadOptions(JsonObject arguments)
    {
        var includeAll = arguments["includeAllKeyboardLayouts"] is JsonValue value &&
                         value.TryGetValue<bool>(out var flag) &&
                         flag;
        return new InputMethodReadOptions(includeAll);
    }

    private static T RunSta<T>(Func<T> func)
    {
        if (!OperatingSystem.IsWindows() ||
            Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return func();
        }

        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }

        return result!;
    }

    private static RuntimeError MapError(Exception exception, string commandId)
    {
        if (exception is RuntimeProtocolException protocol)
        {
            return new RuntimeError(protocol.Code, protocol.Message);
        }

        if (exception is InvalidOperationException)
        {
            return new RuntimeError(
                commandId == "ime-manager.apply" ? "plan.rejected" : "runtime.failed",
                exception.Message);
        }

        if (exception is ArgumentException or JsonException)
        {
            return new RuntimeError("validation.failed", exception.Message);
        }

        if (exception is UnauthorizedAccessException)
        {
            return new RuntimeError("permission.required", exception.Message);
        }

        return new RuntimeError("runtime.failed", exception.Message);
    }

    private static void WriteResponse(
        string requestId,
        string state,
        JsonNode? payload,
        string? errorCode,
        string? errorMessage)
    {
        var error = errorCode is null
            ? null
            : new JsonObject
            {
                ["code"] = errorCode,
                ["message"] = errorMessage ?? ""
            };
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
            ["result"] = new JsonObject
            {
                ["state"] = state,
                ["payload"] = payload,
                ["error"] = error
            }
        };
        Console.Out.WriteLine(response.ToJsonString(ImeManagerJson.Compact));
    }

    private sealed record RuntimeRequest(string RequestId, string CommandId, JsonObject Arguments);

    private sealed record RuntimeError(string Code, string Message);

    private sealed class RuntimeProtocolException : Exception
    {
        public RuntimeProtocolException(string code, string message, Exception? innerException = null)
            : base(message, innerException)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
