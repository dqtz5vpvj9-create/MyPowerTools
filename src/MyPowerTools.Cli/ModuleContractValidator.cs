using System.Text.Json.Nodes;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Protocol;
using MyPowerTools.Runtime;
using MyPowerTools.UI;
using ModuleContext = MyPowerTools.Abstractions.ModuleContext;
using MptCommandDescriptor = MyPowerTools.Abstractions.MptCommandDescriptor;

namespace MyPowerTools.Cli;

public sealed class ModuleContractValidator
{
    private readonly string _schemaRoot;
    private readonly MptHostRuntime _runtime;
    private readonly PackageReader _reader = new();

    public ModuleContractValidator(string schemaRoot, MptHostRuntime runtime)
    {
        _schemaRoot = schemaRoot;
        _runtime = runtime;
    }

    public async Task<ModuleContractValidationReport> ValidateAsync(string packageRoot, CancellationToken cancellationToken)
    {
        var issues = new List<ValidationIssue>();
        var packageRootFullPath = Path.GetFullPath(packageRoot);
        var packages = _reader.DiscoverPackages(packageRootFullPath);

        foreach (var schemaReport in new SchemaPackageValidator(_schemaRoot).ValidatePackageRoot(packageRootFullPath))
        {
            issues.AddRange(schemaReport.Issues.Where(issue => issue.Severity == "error"));
        }

        var uiGate = new UiSurfaceGate();
        foreach (var package in packages)
        {
            issues.AddRange(uiGate.CheckPackage(package).Where(issue => issue.Severity == "error"));
        }

        _runtime.Load(packageRootFullPath);
        await _runtime.RefreshHealthAsync(cancellationToken);
        await _runtime.RefreshDynamicCommandsAsync(cancellationToken);

        var dashboard = _runtime.GetDashboardSnapshot();
        var commands = _runtime.ListCommands("");
        var diagnostics = _runtime.GetRuntimeDiagnostics();
        var moduleReports = new List<ModuleContractModuleReport>();

        foreach (var package in packages)
        {
            foreach (var module in package.Modules)
            {
                var moduleReport = await ValidateModuleAsync(package, module, dashboard, commands, diagnostics, issues, cancellationToken);
                moduleReports.Add(moduleReport);
            }
        }

        foreach (var duplicate in commands.GroupBy(command => command.Id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            issues.Add(new ValidationIssue(packageRootFullPath, "error", $"Duplicate command id '{duplicate.Key}'."));
        }

        return new ModuleContractValidationReport(packages.Count, packages.Sum(package => package.Modules.Count), moduleReports, issues);
    }

    private async Task<ModuleContractModuleReport> ValidateModuleAsync(
        MptPackageDefinition package,
        MptModuleDefinition module,
        DashboardSnapshot dashboard,
        IReadOnlyList<MptCommandDescriptor> commands,
        RuntimeDiagnosticsSnapshot diagnostics,
        List<ValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        var moduleId = module.Manifest.Id;
        var surfaces = ReadSurfaces(package, module, issues);
        var moduleCommands = commands.Where(command => string.Equals(command.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var runtimeModule = diagnostics.Modules.FirstOrDefault(candidate => string.Equals(candidate.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
        var dashboardSurfaceCount = surfaces.Count(surface => string.Equals(surface.Kind, "dashboard-card", StringComparison.OrdinalIgnoreCase));
        var settingsSurfaceCount = surfaces.Count(surface => string.Equals(surface.Kind, "settings", StringComparison.OrdinalIgnoreCase));
        var logsSurfaceCount = surfaces.Count(surface => string.Equals(surface.Kind, "logs", StringComparison.OrdinalIgnoreCase));

        if (dashboardSurfaceCount == 0)
        {
            issues.Add(new ValidationIssue(module.ManifestPath, "error", $"Module '{moduleId}' does not declare a dashboard-card UI surface."));
        }

        if (module.Manifest.Capabilities.Contains("settings", StringComparer.OrdinalIgnoreCase) && settingsSurfaceCount == 0)
        {
            issues.Add(new ValidationIssue(module.ManifestPath, "error", $"Module '{moduleId}' declares settings but has no settings UI surface."));
        }

        if (module.Manifest.Capabilities.Contains("logs", StringComparer.OrdinalIgnoreCase) && logsSurfaceCount == 0)
        {
            issues.Add(new ValidationIssue(module.ManifestPath, "error", $"Module '{moduleId}' declares logs but has no logs UI surface."));
        }

        if (moduleCommands.Length == 0)
        {
            issues.Add(new ValidationIssue(module.ManifestPath, "error", $"Module '{moduleId}' has no indexed commands."));
        }

        if (!dashboard.Cards.Any(card => string.Equals(card.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new ValidationIssue(module.ManifestPath, "error", $"Module '{moduleId}' is missing from the runtime dashboard snapshot."));
        }

        if (runtimeModule is null)
        {
            issues.Add(new ValidationIssue(module.ManifestPath, "error", $"Module '{moduleId}' is missing from runtime diagnostics."));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(runtimeModule.State))
            {
                issues.Add(new ValidationIssue(module.ManifestPath, "error", $"Module '{moduleId}' returned an empty health state."));
            }

            if (runtimeModule.State == "error")
            {
                issues.Add(new ValidationIssue(module.ManifestPath, "error", $"Module '{moduleId}' is in error state."));
            }

            if (runtimeModule.DiagnosticCount == 0)
            {
                issues.Add(new ValidationIssue(module.ManifestPath, "error", $"Module '{moduleId}' returned no typed diagnostics."));
            }
        }

        var logsState = ValidateLogs(module, issues);
        var settingsState = await ValidateSettingsAsync(module, issues, cancellationToken);

        return new ModuleContractModuleReport(
            moduleId,
            runtimeModule?.State ?? "missing",
            moduleCommands.Length,
            surfaces.Count,
            dashboardSurfaceCount > 0,
            settingsState,
            logsState);
    }

    private IReadOnlyList<MptUiSurfaceManifest> ReadSurfaces(MptPackageDefinition package, MptModuleDefinition module, List<ValidationIssue> issues)
    {
        var surfaces = new List<MptUiSurfaceManifest>();
        foreach (var relativePath in module.Manifest.UiSurfaces)
        {
            var path = Path.GetFullPath(Path.Combine(module.Directory, relativePath));
            if (!File.Exists(path))
            {
                issues.Add(new ValidationIssue(path, "error", "UI surface file does not exist."));
                continue;
            }

            try
            {
                var surface = _reader.ReadJson<MptUiSurfaceManifest>(path);
                if (!string.Equals(surface.ModuleId, module.Manifest.Id, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ValidationIssue(path, "error", $"UI surface moduleId '{surface.ModuleId}' does not match '{module.Manifest.Id}'."));
                }

                if (!path.StartsWith(package.Directory, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ValidationIssue(path, "error", "UI surface path escapes package directory."));
                }

                surfaces.Add(surface);
            }
            catch (Exception ex)
            {
                issues.Add(new ValidationIssue(path, "error", ex.Message));
            }
        }

        return surfaces;
    }

    private string ValidateLogs(MptModuleDefinition module, List<ValidationIssue> issues)
    {
        if (!module.Manifest.Capabilities.Contains("logs", StringComparer.OrdinalIgnoreCase))
        {
            return "unsupported";
        }

        try
        {
            _runtime.TailLogs(module.Manifest.Id);
            return "ok";
        }
        catch (Exception ex)
        {
            issues.Add(new ValidationIssue(module.ManifestPath, "error", $"Module '{module.Manifest.Id}' logs provider failed: {ex.Message}"));
            return "failed";
        }
    }

    private async Task<string> ValidateSettingsAsync(MptModuleDefinition module, List<ValidationIssue> issues, CancellationToken cancellationToken)
    {
        if (!module.Manifest.Capabilities.Contains("settings", StringComparer.OrdinalIgnoreCase))
        {
            return "unsupported";
        }

        try
        {
            var settings = _runtime.GetSettings(module.Manifest.Id);
            if (settings.Revision == 0)
            {
                issues.Add(new ValidationIssue(module.ManifestPath, "error", $"Module '{module.Manifest.Id}' settings revision is zero."));
            }
        }
        catch (Exception ex)
        {
            issues.Add(new ValidationIssue(module.ManifestPath, "error", $"Module '{module.Manifest.Id}' settings snapshot failed: {ex.Message}"));
            return "failed";
        }

        if (!module.Manifest.Entrypoints.Any(entry => entry.Kind == "inproc-dotnet"))
        {
            return "static-surface";
        }

        await using var host = new InProcDotNetModuleHost();
        try
        {
            var loaded = await host.LoadAsync(module, CreateModuleContext(module), cancellationToken);
            var schema = await loaded.GetSettingsSchemaAsync(cancellationToken);
            if (!string.Equals(schema.ModuleId, module.Manifest.Id, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ValidationIssue(module.ManifestPath, "error", $"Runtime settings schema moduleId '{schema.ModuleId}' does not match '{module.Manifest.Id}'."));
            }

            JsonNode.Parse(schema.SchemaJson);
            return "runtime-schema";
        }
        catch (Exception ex)
        {
            issues.Add(new ValidationIssue(module.ManifestPath, "error", $"Module '{module.Manifest.Id}' runtime settings schema failed: {ex.Message}"));
            return "failed";
        }
    }

    private static ModuleContext CreateModuleContext(MptModuleDefinition module)
    {
        var root = Path.Combine(Path.GetTempPath(), "MyPowerTools", "contract-runtime", module.Manifest.Id);
        return new ModuleContext(
            ProtocolConstants.HostVersion,
            ProtocolConstants.ModuleProtocolVersion,
            module.Manifest.PackageId,
            module.Manifest.Id,
            Path.Combine(root, "data"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "logs"),
            PlatformId.Current().Rid,
            module.Manifest.Capabilities);
    }
}

public sealed record ModuleContractValidationReport(
    int PackageCount,
    int ModuleCount,
    IReadOnlyList<ModuleContractModuleReport> Modules,
    IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.Severity != "error");
}

public sealed record ModuleContractModuleReport(
    string ModuleId,
    string State,
    int CommandCount,
    int SurfaceCount,
    bool HasDashboardSurface,
    string SettingsState,
    string LogsState);
