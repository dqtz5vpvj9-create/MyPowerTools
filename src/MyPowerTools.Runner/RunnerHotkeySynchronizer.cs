using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Runner;

public sealed class RunnerHotkeySynchronizer
{
    private readonly IHotkeyService _hotkeys;
    private readonly MptHostRuntime _runtime;
    private readonly SemaphoreSlim _sync = new(1, 1);
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
        await _sync.WaitAsync(cancellationToken);
        try { return await SyncCoreAsync(cancellationToken); }
        finally { _sync.Release(); }
    }

    private async Task<IReadOnlyList<RunnerHotkeySyncResult>> SyncCoreAsync(CancellationToken cancellationToken)
    {
        var results = new List<RunnerHotkeySyncResult>();
        var all = _runtime.ListManagedHotkeyBindings();
        // Explicit user bindings win over shipped defaults. Equal-priority conflicts use stable action IDs.
        var bindings = all.DistinctBy(item => item.Gesture, StringComparer.OrdinalIgnoreCase).ToArray();
        var desired = bindings.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var id in _registered.Keys.Where(id => !desired.ContainsKey(id)).ToArray())
        {
            var old = _registered[id];
            var result = await TryUnregisterAsync(old.NativeId, cancellationToken);
            results.Add(new(id, "unregister", result));
            if (result.Success || result.State == "not-registered")
            {
                lock (_gate) { _registered.Remove(id); _commands.Remove(old.NativeId); }
                _runtime.RemoveShortcutRegistration(id);
            }
            else Report(old.Binding, old.Binding.Gesture, "unregister-failed", result.Message);
        }
        foreach (var loser in all.Where(item => !desired.ContainsKey(item.Id)))
        {
            var winner = bindings.First(item => item.Gesture.Equals(loser.Gesture, StringComparison.OrdinalIgnoreCase));
            Report(loser, _registered.GetValueOrDefault(loser.Id)?.Binding.Gesture ?? "", "conflict", $"Same gesture as {winner.Id}; only that binding is registered.");
        }
        foreach (var binding in bindings)
        {
            _registered.TryGetValue(binding.Id, out var existing);
            if (existing is not null && existing.Binding.Gesture.Equals(binding.Gesture, StringComparison.OrdinalIgnoreCase))
            {
                lock (_gate)
                {
                    _commands[existing.NativeId] = new(binding.CommandId, binding.CommandArgsJson);
                    _registered[binding.Id] = existing with { Binding = binding };
                }
                Report(binding, binding.Gesture, "active", "Registered by the operating system.");
                continue;
            }
            var nativeId = existing is null ? binding.Id : binding.Id + "@" + Guid.NewGuid().ToString("N");
            HotkeyRegistrationResult registered;
            try
            {
                registered = await _hotkeys.RegisterAsync(new(nativeId, binding.Gesture, binding.Scope, binding.Reason), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Report(binding, existing?.Binding.Gesture ?? "", existing is null ? "failed" : "kept-previous", ex.Message);
                continue;
            }
            results.Add(new(binding.Id, existing is null ? "register" : "reregister-register", registered));
            if (!registered.Success)
            {
                Report(binding, existing?.Binding.Gesture ?? "", existing is null ? "failed" : "kept-previous",
                    registered.Message + (existing is null ? "" : " The previous gesture is still active."));
                continue;
            }
            if (existing is not null)
            {
                var removed = await TryUnregisterAsync(existing.NativeId, CancellationToken.None);
                results.Add(new(binding.Id, "reregister-unregister", removed));
                if (!removed.Success && removed.State != "not-registered")
                {
                    await TryUnregisterAsync(nativeId, CancellationToken.None);
                    Report(binding, existing.Binding.Gesture, "kept-previous", removed.Message);
                    continue;
                }
            }
            lock (_gate)
            {
                if (existing is not null) _commands.Remove(existing.NativeId);
                _registered[binding.Id] = new(binding, nativeId);
                _commands[nativeId] = new(binding.CommandId, binding.CommandArgsJson);
            }
            Report(binding, binding.Gesture, "active", "Registered by the operating system.");
        }
        return results;
    }

    private async Task<HotkeyRegistrationResult> TryUnregisterAsync(string id, CancellationToken token)
    {
        try { return await _hotkeys.UnregisterAsync(id, token); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, "unregister-failed", ex.Message);
        }
    }

    private void Report(RuntimeHotkeyBinding binding, string actual, string state, string message) =>
        _runtime.SetShortcutRegistration(new(binding.Id, _runtime.GetShortcutIdForBinding(binding.Id), binding.Gesture, actual, state, message));

    public Sdk.CommandRequest? CreateCommandRequest(HotkeyInvocation invocation)
    {
        HotkeyCommandBinding? binding;
        lock (_gate) { _commands.TryGetValue(invocation.Id, out binding); }
        return binding is null ? null : new Sdk.CommandRequest($"hotkey-{Guid.NewGuid():N}", binding.CommandId, ParseCommandArgs(binding.CommandArgsJson));
    }

    public static bool RequiresHotkeySync(string eventType) =>
        eventType is "module.enabled" or "module.disabled" or "settings.updated" or "hotkeys.updated" or "registry.loaded";

    private static JsonObject ParseCommandArgs(string json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new(); }
        catch (JsonException) { return new(); }
    }

    private sealed record RegisteredModuleHotkey(RuntimeHotkeyBinding Binding, string NativeId);
    private sealed record HotkeyCommandBinding(string CommandId, string CommandArgsJson);
}

public sealed record RunnerHotkeySyncResult(string Id, string Operation, HotkeyRegistrationResult Result);
