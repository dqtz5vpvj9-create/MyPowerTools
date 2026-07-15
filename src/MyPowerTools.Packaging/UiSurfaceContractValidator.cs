namespace MyPowerTools.Packaging;

/// <summary>
/// Production-safe validation for declarative UI surface contracts. Pixel rendering,
/// Shell source inspection and screenshot generation live in MyPowerTools.UI.Testing.
/// </summary>
public sealed class UiSurfaceContractValidator
{
    private static readonly HashSet<string> RequiredStates =
    [
        "loading",
        "ready",
        "error"
    ];

    private static readonly HashSet<string> AllowedComponents =
    [
        "MptDashboardCard", "MptModuleHeader", "MptStatusPill", "MptMetricGrid",
        "MptActionBar", "MptCommandButton", "MptSettingsSection", "MptSettingRow",
        "MptDataTable", "MptTimeline", "MptLogViewer", "MptEmptyState",
        "MptErrorView", "MptLoadingView", "MptPermissionPrompt", "MptDiagnosticPanel",
        "MptOverflowMenu", "MptWebViewFrame"
    ];

    private readonly PackageReader _reader = new();

    public IReadOnlyList<ValidationIssue> CheckPackage(MptPackageDefinition package)
    {
        var issues = new List<ValidationIssue>();
        foreach (var module in package.Modules)
        {
            foreach (var surfacePath in module.Manifest.UiSurfaces)
            {
                var fullPath = Path.GetFullPath(Path.Combine(module.Directory, surfacePath));
                if (!File.Exists(fullPath))
                {
                    issues.Add(new ValidationIssue(fullPath, "error", "UI surface file does not exist."));
                    continue;
                }

                var surface = _reader.ReadJson<MptUiSurfaceManifest>(fullPath);
                foreach (var missing in RequiredStates.Except(surface.States, StringComparer.OrdinalIgnoreCase))
                {
                    issues.Add(new ValidationIssue(fullPath, "error", $"Missing required UI state '{missing}'."));
                }

                foreach (var component in surface.Uses.Where(component => !AllowedComponents.Contains(component)))
                {
                    issues.Add(new ValidationIssue(fullPath, "error", $"Component '{component}' is outside the Shell component whitelist."));
                }
            }
        }

        return issues;
    }
}
