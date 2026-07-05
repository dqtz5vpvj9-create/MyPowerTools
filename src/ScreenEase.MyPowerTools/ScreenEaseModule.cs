using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Protocol;
using MyPowerTools.Abstractions;

namespace ScreenEase.MyPowerTools;

public sealed class ScreenEaseModule : IMptModule
{
    private readonly IDisplayService? _displayOverride;
    private ModuleContext? _context;
    private ScreenEaseStore? _store;
    private IDisplayService? _display;

    public string Id => "screenease";
    public string PackageId => "screenease";
    public Version Version => new(0, 2, 0);

    private ScreenEaseStore Store => _store ?? throw new InvalidOperationException("ScreenEase was not initialized.");
    private IDisplayService Display => _display ?? throw new InvalidOperationException("ScreenEase was not initialized.");

    public ScreenEaseModule()
    {
    }

    public ScreenEaseModule(IDisplayService display)
    {
        _displayOverride = display;
    }

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        Directory.CreateDirectory(context.DataDirectory);
        Directory.CreateDirectory(context.CacheDirectory);
        Directory.CreateDirectory(context.LogDirectory);
        _store = new ScreenEaseStore(Path.Combine(context.DataDirectory, "screenease-state.json"));
        _display = _displayOverride ?? CreateDisplayService(context);
        Store.EnsureDefaults();
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["status", "commands", "settings", "logs", "dashboardCard", "detailPage"]));
    }

    public async ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var state = Store.Load();
        var displays = await Display.ListDisplaysAsync(cancellationToken);
        var writer = await Display.GetWriterStatusAsync(cancellationToken);
        var usableDisplays = displays.Where(display => display.State != "unsupported").ToArray();
        var nativeHostReady = state.NativeHost.Enabled && writer.Available;
        var checks = new[]
        {
            new HealthCheckSnapshot("display.enumeration", "Display enumeration", usableDisplays.Length > 0, usableDisplays.Length > 0 ? $"{usableDisplays.Length} display(s) detected." : "No usable display provider was detected."),
            new HealthCheckSnapshot("profile.store", "Profile store", state.Profiles.Count > 0, $"{state.Profiles.Count} profile(s) available; active profile is '{state.ActiveProfileId}'."),
            new HealthCheckSnapshot("rule.store", "Rule store", true, $"{state.Rules.Count} rule(s) configured."),
            new HealthCheckSnapshot("native-host", "Native display writer", nativeHostReady, NativeWriterMessage(state.NativeHost, writer))
        };

        var moduleState = usableDisplays.Length == 0 ? "degraded" : nativeHostReady ? "running" : "degraded";
        var summary = nativeHostReady
            ? $"Profile '{state.ActiveProfileId}' is active across {usableDisplays.Length} display(s)."
            : $"Profile '{state.ActiveProfileId}' is managed; hardware writes are waiting for native display writer readiness.";
        return new ModuleStatusSnapshot(Id, moduleState, summary, DateTimeOffset.UtcNow, checks, 0);
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            Command("screenease.status.summary", "Summarize ScreenEase status", "Display state, active profile, rules, and native host readiness"),
            Command("screenease.displays.list", "List displays", "Enumerate displays through the platform display provider"),
            Command("screenease.profile.list", "List ScreenEase profiles", "Show brightness and color temperature profiles"),
            Command("screenease.profile.plan", "Plan profile application", "Preview display changes for a selected profile", ProfileParameters(includeHardwareWrite: false)),
            Command("screenease.profile.apply", "Apply ScreenEase profile", "Switch active profile and request hardware apply when native host is ready", ProfileParameters(includeHardwareWrite: true)),
            Command("screenease.profile.save", "Save ScreenEase profile", "Persist a profile into ScreenEase shared state", SaveProfileParameters()),
            Command("screenease.rules.status", "Show ScreenEase rules", "Inspect schedule and ambient rule status"),
            Command("screenease.native-writer.status", "Show ScreenEase native writer status", "Probe Windows DDC/CI display write readiness"),
            Command("screenease.native-writer.configure", "Configure ScreenEase native writer", "Enable or disable hardware writes for future profile apply commands",
            [
                new CommandParameterDescriptor("enabled", "Enabled", "boolean", false, "true")
            ])
        ];
        return ValueTask.FromResult(commands);
    }

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return request.CommandId switch
        {
            "screenease.status.summary" => Succeeded(request, (await BuildStatusPayloadAsync(cancellationToken)).ToJsonString()),
            "screenease.displays.list" => Succeeded(request, DisplayListJson(await Display.ListDisplaysAsync(cancellationToken)).ToJsonString()),
            "screenease.profile.list" => Succeeded(request, Store.Load().ProfilesJson().ToJsonString()),
            "screenease.profile.plan" => await PlanProfileAsync(request, cancellationToken),
            "screenease.profile.apply" => await ApplyProfileAsync(request, cancellationToken),
            "screenease.profile.save" => SaveProfile(request),
            "screenease.rules.status" => Succeeded(request, Store.Load().RulesJson().ToJsonString()),
            "screenease.native-writer.status" => Succeeded(request, (await BuildNativeWriterPayloadAsync(cancellationToken)).ToJsonString()),
            "screenease.native-writer.configure" => await ConfigureNativeWriterAsync(request, cancellationToken),
            _ => Failed(request, MptErrorCodes.NotFound, $"Command '{request.CommandId}' is not implemented by ScreenEase.")
        };
    }

    public IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, CancellationToken cancellationToken)
    {
        return EmptyAsyncEnumerable.Of<MptModuleEvent>(cancellationToken);
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """
        {
          "type": "object",
          "properties": {
            "activeProfileId": { "type": "string", "default": "day" },
            "profiles": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["id", "name"],
                "properties": {
                  "id": { "type": "string" },
                  "name": { "type": "string" },
                  "brightness": { "type": "integer", "minimum": 0, "maximum": 100 },
                  "colorTemperature": { "type": "integer", "minimum": 1000, "maximum": 10000 }
                }
              }
            },
            "rules": {
              "type": "array",
              "items": {
                "type": "object",
                "required": ["id", "profileId", "enabled"],
                "properties": {
                  "id": { "type": "string" },
                  "profileId": { "type": "string" },
                  "enabled": { "type": "boolean" },
                  "condition": { "type": "string" }
                }
              }
            },
            "nativeHost": {
              "type": "object",
              "properties": {
                "enabled": { "type": "boolean", "default": false },
                "available": { "type": "boolean", "default": false },
                "state": { "type": "string" },
                "message": { "type": "string" }
              }
            }
          }
        }
        """));
    }

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Store.Load().ToSettingsSnapshot(Id));
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        if (patch.Patch.TryGetPropertyValue("activeProfileId", out var activeNode) && activeNode is not null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(activeNode.GetValue<string>()))
                {
                    messages.Add("activeProfileId cannot be empty.");
                }
            }
            catch (InvalidOperationException)
            {
                messages.Add("activeProfileId must be a string.");
            }
        }

        if (patch.Patch.TryGetPropertyValue("profiles", out var profilesNode) && profilesNode is JsonArray profiles)
        {
            foreach (var profile in profiles.OfType<JsonObject>())
            {
                var parsed = ScreenEaseProfile.FromJson(profile);
                var validation = parsed.Validate();
                messages.AddRange(validation);
            }
        }

        if (patch.Patch.TryGetPropertyValue("nativeHost", out var nativeHostNode) && nativeHostNode is JsonObject nativeHost &&
            nativeHost.TryGetPropertyValue("enabled", out var enabledNode) && enabledNode is not null)
        {
            try
            {
                _ = enabledNode.GetValue<bool>();
            }
            catch (InvalidOperationException)
            {
                messages.Add("nativeHost.enabled must be a boolean.");
            }
        }

        return ValueTask.FromResult(new SettingsValidationResult(
            messages.Count == 0,
            messages,
            messages.Count == 0 ? null : new MptRuntimeError(MptErrorCodes.ValidationFailed, string.Join("; ", messages))));
    }

    public ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        var state = ScreenEaseState.FromSettings(snapshot.Values);
        Store.Save(state);
        return ValueTask.FromResult(state.ToSettingsSnapshot(Id) with { Revision = snapshot.Revision });
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UiSurfaceDescriptor> surfaces =
        [
            new("screenease.dashboard", "dashboard-card", "ScreenEase", new JsonObject { ["moduleId"] = Id }),
            new("screenease.detail", "detail-page", "ScreenEase Profiles", new JsonObject { ["moduleId"] = Id }),
            new("screenease.settings", "settings", "ScreenEase Settings", new JsonObject { ["moduleId"] = Id })
        ];
        return ValueTask.FromResult(surfaces);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    private async Task<CommandExecutionResult> PlanProfileAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var state = Store.Load();
        var profileId = ReadString(request.Args, "profileId") ?? state.ActiveProfileId;
        var profile = state.FindProfile(profileId);
        if (profile is null)
        {
            return Failed(request, MptErrorCodes.NotFound, $"ScreenEase profile '{profileId}' was not found.");
        }

        var displays = await Display.ListDisplaysAsync(cancellationToken);
        return Succeeded(request, BuildPlan(state, profile, displays).ToJsonString());
    }

    private async Task<CommandExecutionResult> ApplyProfileAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var state = Store.Load();
        var profileId = ReadString(request.Args, "profileId") ?? state.ActiveProfileId;
        var profile = state.FindProfile(profileId);
        if (profile is null)
        {
            return Failed(request, MptErrorCodes.NotFound, $"ScreenEase profile '{profileId}' was not found.");
        }

        var displays = await Display.ListDisplaysAsync(cancellationToken);
        var writer = await Display.GetWriterStatusAsync(cancellationToken);
        var hardwareWrite = ReadBool(request.Args, "hardwareWrite") ?? state.NativeHost.Enabled;
        var nativeResult = hardwareWrite
            ? await Display.ApplyProfileAsync(
                new DisplayProfileIntent(profile.Id, ReadString(request.Args, "displayId") ?? "all", profile.Brightness, profile.ColorTemperature, "ScreenEase profile apply"),
                cancellationToken)
            : new BrokerOperationResult(
                false,
                "native-host-required",
                "Native display writes are disabled. Pass hardwareWrite=true for this command or enable the ScreenEase native writer before applying hardware changes.");
        state = state with
        {
            ActiveProfileId = profile.Id,
            NativeHost = new ScreenEaseNativeHostState(state.NativeHost.Enabled, writer.Available, nativeResult.Message),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        Store.Save(state);

        var payload = BuildPlan(state, profile, displays);
        payload["nativeHost"] = BuildNativeHostJson(state.NativeHost, writer, nativeResult, hardwareWrite);

        return Succeeded(request, payload.ToJsonString());
    }

    private async Task<CommandExecutionResult> ConfigureNativeWriterAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var state = Store.Load();
        var enabled = ReadBool(request.Args, "enabled") ?? true;
        var writer = await Display.GetWriterStatusAsync(cancellationToken);
        state = state with
        {
            NativeHost = new ScreenEaseNativeHostState(
                enabled,
                writer.Available,
                enabled ? writer.Message : "Native display writes are disabled by ScreenEase configuration."),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        Store.Save(state);
        return Succeeded(request, BuildNativeHostJson(state.NativeHost, writer, null, false).ToJsonString());
    }

    private CommandExecutionResult SaveProfile(CommandRequest request)
    {
        var profile = ScreenEaseProfile.FromJson(request.Args);
        var validation = profile.Validate();
        if (validation.Count > 0)
        {
            return Failed(request, MptErrorCodes.ValidationFailed, string.Join("; ", validation));
        }

        var state = Store.Load();
        var profiles = state.Profiles
            .Where(item => !string.Equals(item.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
            .Append(profile)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        state = state with { Profiles = profiles, UpdatedAt = DateTimeOffset.UtcNow };
        Store.Save(state);
        return Succeeded(request, profile.ToJson().ToJsonString());
    }

    private async Task<JsonObject> BuildStatusPayloadAsync(CancellationToken cancellationToken)
    {
        var state = Store.Load();
        var displays = await Display.ListDisplaysAsync(cancellationToken);
        var writer = await Display.GetWriterStatusAsync(cancellationToken);
        return new JsonObject
        {
            ["moduleId"] = Id,
            ["activeProfileId"] = state.ActiveProfileId,
            ["activeProfile"] = state.FindProfile(state.ActiveProfileId)?.ToJson(),
            ["displayCount"] = displays.Count,
            ["displays"] = DisplayListJson(displays)["displays"]!.DeepClone(),
            ["profiles"] = state.ProfilesJson()["profiles"]!.DeepClone(),
            ["rules"] = state.RulesJson()["rules"]!.DeepClone(),
            ["nativeHost"] = BuildNativeHostJson(state.NativeHost, writer, null, false)
        };
    }

    private async Task<JsonObject> BuildNativeWriterPayloadAsync(CancellationToken cancellationToken)
    {
        var state = Store.Load();
        var writer = await Display.GetWriterStatusAsync(cancellationToken);
        return BuildNativeHostJson(state.NativeHost, writer, null, false);
    }

    private static JsonObject BuildNativeHostJson(
        ScreenEaseNativeHostState configured,
        DisplayWriterStatus writer,
        BrokerOperationResult? applyResult,
        bool hardwareWriteRequested)
    {
        return new JsonObject
        {
            ["enabled"] = configured.Enabled,
            ["available"] = writer.Available,
            ["state"] = applyResult?.State ?? writer.State,
            ["success"] = applyResult?.Success,
            ["hardwareWriteRequested"] = hardwareWriteRequested,
            ["message"] = applyResult?.Message ?? NativeWriterMessage(configured, writer)
        };
    }

    private static string NativeWriterMessage(ScreenEaseNativeHostState configured, DisplayWriterStatus writer)
    {
        if (!configured.Enabled)
        {
            return writer.Available
                ? "Native display writer is detected; hardware writes are disabled until ScreenEase native writer is enabled."
                : writer.Message;
        }

        return writer.Available
            ? writer.Message
            : $"Native display writer is enabled but unavailable: {writer.Message}";
    }

    private static JsonObject BuildPlan(ScreenEaseState state, ScreenEaseProfile profile, IReadOnlyList<DisplaySnapshot> displays)
    {
        var actions = new JsonArray();
        foreach (var display in displays.Where(display => display.State != "unsupported"))
        {
            actions.Add(new JsonObject
            {
                ["displayId"] = display.Id,
                ["displayName"] = display.Name,
                ["profileId"] = profile.Id,
                ["brightness"] = profile.Brightness,
                ["colorTemperature"] = profile.ColorTemperature,
                ["nativeAction"] = "screen-profile.apply"
            });
        }

        return new JsonObject
        {
            ["activeProfileId"] = state.ActiveProfileId,
            ["profile"] = profile.ToJson(),
            ["displayCount"] = displays.Count,
            ["expectedChange"] = new JsonObject
            {
                ["actions"] = actions
            },
            ["rules"] = state.RulesJson()["rules"]!.DeepClone()
        };
    }

    private static JsonObject DisplayListJson(IReadOnlyList<DisplaySnapshot> displays)
    {
        var array = new JsonArray();
        foreach (var display in displays)
        {
            array.Add(new JsonObject
            {
                ["id"] = display.Id,
                ["name"] = display.Name,
                ["state"] = display.State,
                ["width"] = display.Width,
                ["height"] = display.Height,
                ["refreshRateHz"] = display.RefreshRateHz,
                ["orientation"] = display.Orientation,
                ["primary"] = display.Primary,
                ["detail"] = display.Detail
            });
        }

        return new JsonObject
        {
            ["displayCount"] = displays.Count,
            ["displays"] = array
        };
    }

    private MptCommandDescriptor Command(string id, string title, string subtitle, IReadOnlyList<CommandParameterDescriptor>? parameters = null)
    {
        return new MptCommandDescriptor(id, Id, title, subtitle, "action", Category: "ScreenEase", Execution: new JsonObject { ["type"] = "module.execute" }, Parameters: parameters);
    }

    private static IReadOnlyList<CommandParameterDescriptor> ProfileParameters(bool includeHardwareWrite)
    {
        var parameters = new List<CommandParameterDescriptor>
        {
            new("profileId", "Profile ID", "text", false, ""),
            new("displayId", "Display ID", "text", false, "all")
        };
        if (includeHardwareWrite)
        {
            parameters.Add(new CommandParameterDescriptor("hardwareWrite", "Hardware write", "boolean", false, "false"));
        }

        return parameters;
    }

    private static IReadOnlyList<CommandParameterDescriptor> SaveProfileParameters()
    {
        return
        [
            new CommandParameterDescriptor("id", "Profile ID", "text", true, ""),
            new CommandParameterDescriptor("name", "Name", "text", true, ""),
            new CommandParameterDescriptor("brightness", "Brightness", "number", false, "70"),
            new CommandParameterDescriptor("colorTemperature", "Color temperature", "number", false, "5200")
        ];
    }

    private static CommandExecutionResult Succeeded(CommandRequest request, string output)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, output);
    }

    private static CommandExecutionResult Failed(CommandRequest request, string code, string message)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "failed", false, "", new MptRuntimeError(code, message));
    }

    private static string? ReadString(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool? ReadBool(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IDisplayService CreateDisplayService(ModuleContext context)
    {
        if (context.TryGetCapability<IDisplayService>("display.profile", out var display))
        {
            return display;
        }

        return new UnsupportedDisplayService(
            "display.profile",
            "No display capability provider was injected by the host runtime.");
    }
}

internal sealed class ScreenEaseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public ScreenEaseStore(string path)
    {
        _path = path;
    }

    public void EnsureDefaults()
    {
        if (!File.Exists(_path))
        {
            Save(ScreenEaseState.Default());
        }
    }

    public ScreenEaseState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return ScreenEaseState.Default();
            }

            return JsonSerializer.Deserialize<ScreenEaseState>(File.ReadAllText(_path), JsonOptions) ?? ScreenEaseState.Default();
        }
        catch
        {
            return ScreenEaseState.Default();
        }
    }

    public void Save(ScreenEaseState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}

internal sealed record ScreenEaseState(
    string ActiveProfileId,
    IReadOnlyList<ScreenEaseProfile> Profiles,
    IReadOnlyList<ScreenEaseRule> Rules,
    ScreenEaseNativeHostState NativeHost,
    DateTimeOffset UpdatedAt)
{
    public static ScreenEaseState Default()
    {
        return new ScreenEaseState(
            "day",
            [
                new ScreenEaseProfile("day", "Day", 85, 6500),
                new ScreenEaseProfile("night", "Night", 45, 4200),
                new ScreenEaseProfile("focus", "Focus", 70, 5200)
            ],
            [
                new ScreenEaseRule("evening", "night", true, "local-time >= 20:00"),
                new ScreenEaseRule("morning", "day", true, "local-time >= 08:00")
            ],
            new ScreenEaseNativeHostState(false, false, "ScreenEase native host is pending; state/profile management is available."),
            DateTimeOffset.UtcNow);
    }

    public static ScreenEaseState FromSettings(JsonObject values)
    {
        var defaults = Default();
        var profiles = values.TryGetPropertyValue("profiles", out var profilesNode) && profilesNode is JsonArray profilesArray
            ? profilesArray.OfType<JsonObject>().Select(ScreenEaseProfile.FromJson).Where(profile => profile.Validate().Count == 0).ToArray()
            : defaults.Profiles;
        if (profiles.Count == 0)
        {
            profiles = defaults.Profiles;
        }

        var rules = values.TryGetPropertyValue("rules", out var rulesNode) && rulesNode is JsonArray rulesArray
            ? rulesArray.OfType<JsonObject>().Select(ScreenEaseRule.FromJson).ToArray()
            : defaults.Rules;
        var activeProfileId = SettingsJson.ReadString(values, "activeProfileId") ?? defaults.ActiveProfileId;
        if (!profiles.Any(profile => string.Equals(profile.Id, activeProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            activeProfileId = profiles[0].Id;
        }

        var nativeHost = values.TryGetPropertyValue("nativeHost", out var nativeHostNode) && nativeHostNode is JsonObject nativeHostObject
            ? ScreenEaseNativeHostState.FromJson(nativeHostObject)
            : defaults.NativeHost;

        return new ScreenEaseState(activeProfileId, profiles, rules, nativeHost, DateTimeOffset.UtcNow);
    }

    public ScreenEaseProfile? FindProfile(string id)
    {
        return Profiles.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public SettingsSnapshotDocument ToSettingsSnapshot(string moduleId)
    {
        return new SettingsSnapshotDocument(moduleId, 1, new JsonObject
        {
            ["activeProfileId"] = ActiveProfileId,
            ["profiles"] = ProfilesJson()["profiles"]!.DeepClone(),
            ["rules"] = RulesJson()["rules"]!.DeepClone(),
            ["nativeHost"] = NativeHost.ToJson()
        }, UpdatedAt);
    }

    public JsonObject ProfilesJson()
    {
        var array = new JsonArray();
        foreach (var profile in Profiles)
        {
            array.Add(profile.ToJson());
        }

        return new JsonObject
        {
            ["activeProfileId"] = ActiveProfileId,
            ["profiles"] = array
        };
    }

    public JsonObject RulesJson()
    {
        var array = new JsonArray();
        foreach (var rule in Rules)
        {
            array.Add(rule.ToJson(FindProfile(rule.ProfileId)?.Name ?? ""));
        }

        return new JsonObject
        {
            ["rules"] = array
        };
    }
}

internal sealed record ScreenEaseProfile(string Id, string Name, int Brightness, int ColorTemperature)
{
    public IReadOnlyList<string> Validate()
    {
        var messages = new List<string>();
        if (string.IsNullOrWhiteSpace(Id))
        {
            messages.Add("profile id is required.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            messages.Add("profile name is required.");
        }

        if (Brightness is < 0 or > 100)
        {
            messages.Add("brightness must be between 0 and 100.");
        }

        if (ColorTemperature is < 1000 or > 10000)
        {
            messages.Add("colorTemperature must be between 1000 and 10000.");
        }

        return messages;
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["id"] = Id,
            ["name"] = Name,
            ["brightness"] = Brightness,
            ["colorTemperature"] = ColorTemperature
        };
    }

    public static ScreenEaseProfile FromJson(JsonObject node)
    {
        return new ScreenEaseProfile(
            ReadString(node, "id") ?? "",
            ReadString(node, "name") ?? ReadString(node, "id") ?? "",
            ReadInt(node, "brightness") ?? 70,
            ReadInt(node, "colorTemperature") ?? 5200);
    }

    private static string? ReadString(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            try
            {
                return checked((int)node.GetValue<long>());
            }
            catch
            {
                return null;
            }
        }
    }
}

internal sealed record ScreenEaseRule(string Id, string ProfileId, bool Enabled, string Condition)
{
    public static ScreenEaseRule FromJson(JsonObject node)
    {
        return new ScreenEaseRule(
            SettingsJson.ReadString(node, "id") ?? "",
            SettingsJson.ReadString(node, "profileId") ?? "",
            SettingsJson.ReadBool(node, "enabled") ?? true,
            SettingsJson.ReadString(node, "condition") ?? "");
    }

    public JsonObject ToJson(string profileName)
    {
        return new JsonObject
        {
            ["id"] = Id,
            ["profileId"] = ProfileId,
            ["profileName"] = profileName,
            ["enabled"] = Enabled,
            ["condition"] = Condition,
            ["state"] = Enabled ? "ready" : "disabled"
        };
    }
}

internal sealed record ScreenEaseNativeHostState(bool Enabled, bool Available, string Message)
{
    public static ScreenEaseNativeHostState FromJson(JsonObject node)
    {
        return new ScreenEaseNativeHostState(
            SettingsJson.ReadBool(node, "enabled") ?? false,
            SettingsJson.ReadBool(node, "available") ?? false,
            SettingsJson.ReadString(node, "message") ?? "ScreenEase native host is pending; state/profile management is available.");
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["enabled"] = Enabled,
            ["available"] = Available,
            ["state"] = Available ? "ready" : "native-host-required",
            ["message"] = Message
        };
    }
}

