using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Runner;

public sealed class RunnerHotkeySynchronizer
{
    private readonly IHotkeyService _hotkeys;
    private readonly MptHostRuntime _runtime;
    private readonly object _gate = new();
    private readonly Dictionary<string, RegisteredModuleHotkey> _registered = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HotkeyCommandBinding> _commands = new(StringComparer.OrdinalIgnoreCase);

    public RunnerHotkeySynchronizer(IHotkeyService hotkeys, MptHostRuntime runtime)
    {
        _hotkeys = hotkeys;
        _runtime = runtime;
    }

    public async Task<IReadOnlyList<RunnerHotkeySyncResult>> SyncAsync(CancellationToken cancellationToken)
    {
        var results = new List<RunnerHotkeySyncResult>();
        var bindings = _runtime.ListHotkeyBindings();
        var nextById = bindings.ToDictionary(binding => binding.Id, StringComparer.OrdinalIgnoreCase);
        List<string> staleIds;
        lock (_gate)
        {
            staleIds = _registered.Keys.Where(id => !nextById.ContainsKey(id)).ToList();
        }

        foreach (var id in staleIds)
        {
            var unregister = await _hotkeys.UnregisterAsync(id, cancellationToken);
            lock (_gate)
            {
                _registered.Remove(id);
                _commands.Remove(id);
            }

            results.Add(new RunnerHotkeySyncResult(id, "unregister", unregister));
        }

        foreach (var binding in bindings)
        {
            RegisteredModuleHotkey? existing;
            lock (_gate)
            {
                _commands[binding.Id] = new HotkeyCommandBinding(binding.CommandId, binding.CommandArgsJson);
                _registered.TryGetValue(binding.Id, out existing);
            }

            var normalizedGesture = binding.Gesture.Trim();
            if (existing is not null &&
                string.Equals(existing.Gesture, normalizedGesture, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (existing is not null)
            {
                var unregister = await _hotkeys.UnregisterAsync(binding.Id, cancellationToken);
                lock (_gate)
                {
                    _registered.Remove(binding.Id);
                }

                results.Add(new RunnerHotkeySyncResult(binding.Id, "reregister-unregister", unregister));
                if (!unregister.Success && !string.Equals(unregister.State, "not-registered", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var register = await _hotkeys.RegisterAsync(
                new HotkeyRegistration(binding.Id, normalizedGesture, binding.Scope, binding.Reason),
                cancellationToken);
            if (register.Success)
            {
                lock (_gate)
                {
                    _registered[binding.Id] = new RegisteredModuleHotkey(normalizedGesture);
                    _commands[binding.Id] = new HotkeyCommandBinding(binding.CommandId, binding.CommandArgsJson);
                }
            }

            results.Add(new RunnerHotkeySyncResult(binding.Id, existing is null ? "register" : "reregister-register", register));
        }

        return results;
    }

    public Sdk.CommandRequest? CreateCommandRequest(HotkeyInvocation invocation)
    {
        HotkeyCommandBinding? binding;
        lock (_gate)
        {
            _commands.TryGetValue(invocation.Id, out binding);
        }

        if (binding is null)
        {
            return null;
        }

        return new Sdk.CommandRequest(
            $"hotkey-{Guid.NewGuid():N}",
            binding.CommandId,
            ParseCommandArgs(binding.CommandArgsJson));
    }

    public static bool RequiresHotkeySync(string eventType)
    {
        return eventType is "module.enabled" or "module.disabled" or "settings.updated" or "hotkeys.updated" or "registry.loaded";
    }

    private static JsonObject ParseCommandArgs(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private sealed record RegisteredModuleHotkey(string Gesture);
    private sealed record HotkeyCommandBinding(string CommandId, string CommandArgsJson);
}

public sealed record RunnerHotkeySyncResult(string Id, string Operation, HotkeyRegistrationResult Result);
