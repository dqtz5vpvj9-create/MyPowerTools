using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public static class ShellPageRefreshRouter
{
    public static ShellPageRefreshPlan Route(string currentPage, HostProto.HostEvent evt)
    {
        return evt.Type switch
        {
            "notification.created" when currentPage == "Notifications" => new(ReloadNotifications: true),
            "command.executed" => new(
                ReloadBrokerAudit: true,
                ReloadCurrentPage: currentPage is "Dashboard" or "Diagnostics"),
            "settings.updated" when currentPage == "Settings" => new(ReloadSettingsModuleId: evt.SourceId),
            "shortcuts.updated" or "hotkeys.updated" => new(ReloadShortcutCatalog: true),
            "module.enabled" or "module.disabled" or "registry.loaded" or "commands.dynamic.refreshed" => new(
                ReloadCommands: true,
                ReloadShortcutCatalog: true,
                ReloadHomeTools: evt.Type == "registry.loaded",
                ReloadCurrentPage: currentPage is "Dashboard" or "Modules" or "Packages" or "Diagnostics"
                    or "Tools"),
            "runtime.process.restart" or "runtime.process.policy" or "runtime.process.policy.expired"
                when currentPage == "Diagnostics" => new(ReloadDiagnostics: true),
            "module.health.changed" => new(ReloadCurrentPage: currentPage is "Dashboard" or "Modules" or "Diagnostics"),
            _ => ShellPageRefreshPlan.None
        };
    }
}

public sealed record ShellPageRefreshPlan(
    bool ReloadNotifications = false,
    bool ReloadBrokerAudit = false,
    bool ReloadCommands = false,
    bool ReloadHomeTools = false,
    bool ReloadCurrentPage = false,
    bool ReloadDiagnostics = false,
    bool ReloadShortcutCatalog = false,
    string? ReloadSettingsModuleId = null)
{
    public static ShellPageRefreshPlan None { get; } = new();
}
