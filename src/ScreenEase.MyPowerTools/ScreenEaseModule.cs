using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Linux;
using MyPowerTools.Platform.Mac;
using MyPowerTools.Platform.Windows;
using MyPowerTools.Protocol;
using MyPowerTools.Runtime;

namespace ScreenEase.MyPowerTools;

public sealed class ScreenEaseModule : IMptModule
{
    private ModuleContext? _context;
    private ScreenEaseStore? _store;
    private IDisplayService? _display;

    public string Id => "screenease";
    public string PackageId => "screenease";
    public Version Version => new(0, 2, 0);

    private ScreenEaseStore Store => _store ?? throw new InvalidOperationException("ScreenEase was not initialized.");
    private IDisplayService Display => _display ?? throw new InvalidOperationException("ScreenEase was not initialized.");

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _context = context;
        Directory.CreateDirectory(context.DataDirectory);
        Directory.CreateDirectory(context.CacheDirectory);
        Directory.CreateDirectory(context.LogDirectory);
        _store = new ScreenEaseStore(Path.Combine(context.DataDirectory, "screenease-state.json"));
        _display = CreateDisplayService();
        Store.EnsureDefaults();
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["status", "commands", "settings", "logs", "dashboardCard", "detailPage"]));
    }

    public async ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var state = Store.Load();
        var displays = await Display.ListDisplaysAsync(cancellationToken);
        var usableDisplays = displays.Where(display => display.State != "unsupported").ToArray();
        var nativeHostReady = state.NativeHost.Enabled && state.NativeHost.Available;
        var checks = new[]
        {
            new HealthCheckSnapshot("display.enumeration", "Display enumeration", usableDisplays.Length > 0, usableDisplays.Length > 0 ? $"{usableDisplays.Length} display(s) detected." : "No usable display provider was detected."),
            new HealthCheckSnapshot("profile.store", "Profile store", state.Profiles.Count > 0, $"{state.Profiles.Count} profile(s) available; active profile is '{state.ActiveProfileId}'."),
            new HealthCheckSnapshot("rule.store", "Rule store", true, $"{state.Rules.Count} rule(s) configured."),
            new HealthCheckSnapshot("native-host", "Native display writer", nativeHostReady, nativeHostReady ? "Native display writer is available." : "Brightness and color-temperature writes require the ScreenEase native host.")
        };

        var moduleState = usableDisplays.Length == 0 ? "degraded" : nativeHostReady ? "running" : "degraded";
        var summary = nativeHostReady
            ? $"Profile '{state.ActiveProfileId}' is active across {usableDisplays.Length} display(s)."
            : $"Profile '{state.ActiveProfileId}' is managed; hardware writes are waiting for the native display host.";
        return new ModuleStatusSnapshot(Id, moduleState, summary, DateTimeOffset.UtcNow, checks, 0);
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            Command("screenease.status.summary", "Summarize ScreenEase status", "Display state, active profile, rules, and native host readiness"),
            Command("screenease.displays.list", "List displays", "Enumerate displays through the platform display provider"),
            Command("screenease.profile.list", "List ScreenEase profiles", "Show brightness and color temperature profiles"),
            Command("screenease.profile.plan", "Plan profile application", "Preview display changes for a selected profile"),
            Command("screenease.profile.apply", "Apply ScreenEase profile", "Switch active profile and request hardware apply when native host is ready"),
            Command("screenease.profile.save", "Save ScreenEase profile", "Persist a profile into ScreenEase shared state"),
            Command("screenease.rules.status", "Show ScreenEase rules", "Inspect schedule and ambient rule status")
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

        return ValueTask.FromResult(new SettingsValidationResult(
            messages.Count == 0,
            messages,
            messages.Count == 0 ? null : new MptRuntimeError(MptErrorCodes.ValidationFailed, string.Join("; ", messages))));
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
        state = state with { ActiveProfileId = profile.Id, UpdatedAt = DateTimeOffset.UtcNow };
        Store.Save(state);

        var nativeResult = await Display.ApplyProfileAsync(
            new DisplayProfileIntent(profile.Id, ReadString(request.Args, "displayId") ?? "all", profile.Brightness, profile.ColorTemperature, "ScreenEase profile apply"),
            cancellationToken);
        var payload = BuildPlan(state, profile, displays);
        payload["nativeHost"] = new JsonObject
        {
            ["success"] = nativeResult.Success,
            ["state"] = nativeResult.State,
            ["message"] = nativeResult.Message
        };

        return Succeeded(request, payload.ToJsonString());
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
        return new JsonObject
        {
            ["moduleId"] = Id,
            ["activeProfileId"] = state.ActiveProfileId,
            ["activeProfile"] = state.FindProfile(state.ActiveProfileId)?.ToJson(),
            ["displayCount"] = displays.Count,
            ["displays"] = DisplayListJson(displays)["displays"]!.DeepClone(),
            ["profiles"] = state.ProfilesJson()["profiles"]!.DeepClone(),
            ["rules"] = state.RulesJson()["rules"]!.DeepClone(),
            ["nativeHost"] = state.NativeHost.ToJson()
        };
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

    private MptCommandDescriptor Command(string id, string title, string subtitle)
    {
        return new MptCommandDescriptor(id, Id, title, subtitle, "action", Category: "ScreenEase", Execution: new JsonObject { ["type"] = "module.execute" });
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

    private static IDisplayService CreateDisplayService()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsPlatformPack().Display;
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacPlatformPack().Display;
        }

        return new LinuxPlatformPack().Display;
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
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["enabled"] = Enabled,
            ["available"] = Available,
            ["message"] = Message
        };
    }
}
