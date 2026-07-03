namespace MyPowerTools.Runtime;

public sealed class CommandIndex
{
    private readonly List<MptCommandDescriptor> _commands = [];
    private readonly StaticCommandIndexReader _staticReader = new();

    public IReadOnlyList<MptCommandDescriptor> Commands => _commands;

    public void Rebuild(IEnumerable<RuntimeModuleRecord> modules, IEnumerable<MptCommandDescriptor>? dynamicCommands = null)
    {
        _commands.Clear();

        foreach (var module in modules.OrderBy(module => module.Module.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var id = module.Module.Manifest.Id;
            AddOrReplace(new MptCommandDescriptor(
                $"{id}.open",
                id,
                $"Open {module.Module.Manifest.DisplayName}",
                module.Module.Manifest.DisplayName,
                "open",
                Execution: new() { ["type"] = "open", ["surface"] = "detail" }));
            AddOrReplace(new MptCommandDescriptor(
                $"{id}.status.refresh",
                id,
                $"Refresh {module.Module.Manifest.DisplayName}",
                "Update status snapshot",
                "action",
                Execution: new() { ["type"] = "host.status.refresh" }));

            if (module.Module.Manifest.Capabilities.Contains("settings", StringComparer.OrdinalIgnoreCase))
            {
                AddOrReplace(new MptCommandDescriptor(
                    $"{id}.settings.open",
                    id,
                    $"Open {module.Module.Manifest.DisplayName} settings",
                    "Settings Center",
                    "open",
                    Execution: new() { ["type"] = "open", ["surface"] = "settings" }));
            }

            foreach (var command in _staticReader.Read(module))
            {
                AddOrReplace(command);
            }
        }

        if (dynamicCommands is not null)
        {
            foreach (var command in dynamicCommands)
            {
                AddOrReplace(command);
            }
        }
    }

    public IReadOnlyList<MptCommandDescriptor> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _commands;
        }

        return _commands
            .Where(command =>
                command.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                command.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                command.ModuleId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public MptCommandDescriptor? Find(string commandId)
    {
        return _commands.FirstOrDefault(command => string.Equals(command.Id, commandId, StringComparison.OrdinalIgnoreCase));
    }

    private void AddOrReplace(MptCommandDescriptor command)
    {
        var existing = _commands.FindIndex(candidate => string.Equals(candidate.Id, command.Id, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            _commands[existing] = command;
            return;
        }

        _commands.Add(command);
    }
}
