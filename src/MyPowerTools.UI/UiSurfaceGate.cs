using MyPowerTools.Packaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyPowerTools.UI;

public sealed class UiSurfaceGate
{
    private static readonly HashSet<string> RequiredStates =
    [
        "loading",
        "ready",
        "error"
    ];

    private static readonly HashSet<string> AllowedComponents =
    [
        "MptDashboardCard",
        "MptModuleHeader",
        "MptStatusPill",
        "MptMetricGrid",
        "MptActionBar",
        "MptCommandButton",
        "MptSettingsSection",
        "MptSettingRow",
        "MptDataTable",
        "MptTimeline",
        "MptLogViewer",
        "MptEmptyState",
        "MptErrorView",
        "MptLoadingView",
        "MptPermissionPrompt",
        "MptDiagnosticPanel",
        "MptOverflowMenu",
        "MptWebViewFrame"
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

    public string WriteDefaultSnapshotSet(string outputDirectory)
    {
        var packageRoot = Path.Combine(Directory.GetCurrentDirectory(), "modules");
        return Directory.Exists(packageRoot)
            ? WriteSnapshotSet(packageRoot, outputDirectory, new UiSnapshotRequest("*", "light", "1920x1080", "normal"))
            : WriteEmptySnapshotSet(outputDirectory, new UiSnapshotRequest("*", "light", "1920x1080", "normal"));
    }

    public string WriteSnapshotSet(string packageRoot, string outputDirectory, UiSnapshotRequest request)
    {
        Directory.CreateDirectory(outputDirectory);
        var entries = new JsonArray();
        foreach (var package in _reader.DiscoverPackages(packageRoot))
        {
            foreach (var module in package.Modules)
            {
                foreach (var surfacePath in module.Manifest.UiSurfaces)
                {
                    var fullPath = Path.GetFullPath(Path.Combine(module.Directory, surfacePath));
                    if (!File.Exists(fullPath))
                    {
                        continue;
                    }

                    var surface = _reader.ReadJson<MptUiSurfaceManifest>(fullPath);
                    if (!MatchesSurfaceFilter(surface, request.Surface))
                    {
                        continue;
                    }

                    var source = File.ReadAllText(fullPath);
                    var snapshotBaseName = $"{Sanitize(surface.SurfaceId)}.{Sanitize(request.Theme)}.{Sanitize(request.Density)}.{Sanitize(request.Size)}.snapshot";
                    var snapshotName = $"{snapshotBaseName}.json";
                    var pixelSnapshotName = $"{snapshotBaseName}.png";
                    var snapshotPath = Path.Combine(outputDirectory, snapshotName);
                    var pixelSnapshotPath = Path.Combine(outputDirectory, pixelSnapshotName);
                    var sourceSha256 = Sha256(source);
                    var pixel = PngSurfaceSnapshotWriter.Write(pixelSnapshotPath, pixelSnapshotName, request, package.Package.Id, surface);
                    var snapshot = new JsonObject
                    {
                        ["schemaVersion"] = "1.0",
                        ["surfaceId"] = surface.SurfaceId,
                        ["moduleId"] = surface.ModuleId,
                        ["packageId"] = package.Package.Id,
                        ["packageVersion"] = package.Package.Version,
                        ["kind"] = surface.Kind,
                        ["theme"] = request.Theme,
                        ["density"] = request.Density,
                        ["size"] = request.Size,
                        ["layout"] = new JsonObject
                        {
                            ["mode"] = surface.Layout.Mode,
                            ["preferredWidth"] = surface.Layout.PreferredWidth,
                            ["preferredHeight"] = surface.Layout.PreferredHeight,
                            ["minWidth"] = surface.Layout.MinWidth,
                            ["minHeight"] = surface.Layout.MinHeight
                        },
                        ["uses"] = ToJsonArray(surface.Uses),
                        ["states"] = ToJsonArray(surface.States),
                        ["sourceSha256"] = sourceSha256,
                        ["pixelSnapshot"] = pixel.FileName,
                        ["pixelSha256"] = pixel.Sha256,
                        ["pixelWidth"] = pixel.Width,
                        ["pixelHeight"] = pixel.Height,
                        ["pixelUniqueColorCount"] = pixel.UniqueColorCount,
                        ["pixelNonBackgroundPixels"] = pixel.NonBackgroundPixels
                    };
                    File.WriteAllText(snapshotPath, snapshot.ToJsonString(JsonOptions));

                    entries.Add(new JsonObject
                    {
                        ["surfaceId"] = surface.SurfaceId,
                        ["moduleId"] = surface.ModuleId,
                        ["kind"] = surface.Kind,
                        ["snapshot"] = snapshotName,
                        ["sourceSha256"] = sourceSha256,
                        ["pixelSnapshot"] = pixel.FileName,
                        ["pixelSha256"] = pixel.Sha256,
                        ["pixelWidth"] = pixel.Width,
                        ["pixelHeight"] = pixel.Height,
                        ["pixelUniqueColorCount"] = pixel.UniqueColorCount,
                        ["pixelNonBackgroundPixels"] = pixel.NonBackgroundPixels
                    });
                }
            }
        }

        var manifestPath = Path.Combine(outputDirectory, "ui-snapshot-manifest.json");
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["packageRoot"] = Path.GetFullPath(packageRoot),
            ["surface"] = request.Surface,
            ["theme"] = request.Theme,
            ["density"] = request.Density,
            ["size"] = request.Size,
            ["snapshotCount"] = entries.Count,
            ["pixelSnapshotCount"] = entries.Count,
            ["snapshots"] = entries
        };
        File.WriteAllText(manifestPath, manifest.ToJsonString(JsonOptions));
        return manifestPath;
    }

    public string WriteShellSnapshotSet(string outputDirectory, UiSnapshotRequest request)
    {
        Directory.CreateDirectory(outputDirectory);
        var entries = new JsonArray();
        var surfaces = CreateShellSurfaces()
            .Where(surface => MatchesSurfaceFilter(surface, request.Surface))
            .ToArray();

        foreach (var surface in surfaces)
        {
            var source = JsonSerializer.Serialize(surface, JsonOptions);
            var snapshotBaseName = $"{Sanitize(surface.SurfaceId)}.{Sanitize(request.Theme)}.{Sanitize(request.Density)}.{Sanitize(request.Size)}.snapshot";
            var snapshotName = $"{snapshotBaseName}.json";
            var pixelSnapshotName = $"{snapshotBaseName}.png";
            var snapshotPath = Path.Combine(outputDirectory, snapshotName);
            var pixelSnapshotPath = Path.Combine(outputDirectory, pixelSnapshotName);
            var sourceSha256 = Sha256(source);
            var pixel = PngSurfaceSnapshotWriter.Write(pixelSnapshotPath, pixelSnapshotName, request, "shell", surface);
            var snapshot = new JsonObject
            {
                ["schemaVersion"] = "1.0",
                ["surfaceId"] = surface.SurfaceId,
                ["moduleId"] = surface.ModuleId,
                ["packageId"] = "shell",
                ["kind"] = surface.Kind,
                ["theme"] = request.Theme,
                ["density"] = request.Density,
                ["size"] = request.Size,
                ["layout"] = new JsonObject
                {
                    ["mode"] = surface.Layout.Mode,
                    ["preferredWidth"] = surface.Layout.PreferredWidth,
                    ["preferredHeight"] = surface.Layout.PreferredHeight,
                    ["minWidth"] = surface.Layout.MinWidth,
                    ["minHeight"] = surface.Layout.MinHeight
                },
                ["uses"] = ToJsonArray(surface.Uses),
                ["states"] = ToJsonArray(surface.States),
                ["keyboardShortcuts"] = ToJsonArray(ShellKeyboardShortcutsFor(surface.SurfaceId)),
                ["focusStates"] = ToJsonArray(ShellFocusStatesFor(surface.SurfaceId)),
                ["sourceSha256"] = sourceSha256,
                ["pixelSnapshot"] = pixel.FileName,
                ["pixelSha256"] = pixel.Sha256,
                ["pixelWidth"] = pixel.Width,
                ["pixelHeight"] = pixel.Height,
                ["pixelUniqueColorCount"] = pixel.UniqueColorCount,
                ["pixelNonBackgroundPixels"] = pixel.NonBackgroundPixels
            };
            File.WriteAllText(snapshotPath, snapshot.ToJsonString(JsonOptions));

            entries.Add(new JsonObject
            {
                ["surfaceId"] = surface.SurfaceId,
                ["moduleId"] = surface.ModuleId,
                ["kind"] = surface.Kind,
                ["snapshot"] = snapshotName,
                ["states"] = ToJsonArray(surface.States),
                ["keyboardShortcuts"] = ToJsonArray(ShellKeyboardShortcutsFor(surface.SurfaceId)),
                ["focusStates"] = ToJsonArray(ShellFocusStatesFor(surface.SurfaceId)),
                ["sourceSha256"] = sourceSha256,
                ["pixelSnapshot"] = pixel.FileName,
                ["pixelSha256"] = pixel.Sha256,
                ["pixelWidth"] = pixel.Width,
                ["pixelHeight"] = pixel.Height,
                ["pixelUniqueColorCount"] = pixel.UniqueColorCount,
                ["pixelNonBackgroundPixels"] = pixel.NonBackgroundPixels
            });
        }

        var manifestPath = Path.Combine(outputDirectory, "shell-ui-snapshot-manifest.json");
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["surface"] = request.Surface,
            ["theme"] = request.Theme,
            ["density"] = request.Density,
            ["size"] = request.Size,
            ["snapshotCount"] = entries.Count,
            ["pixelSnapshotCount"] = entries.Count,
            ["requiredSurfaceCount"] = RequiredShellSurfaceIds.Length,
            ["requiredSurfaces"] = ToJsonArray(RequiredShellSurfaceIds),
            ["keyboardNavigation"] = CreateShellKeyboardNavigationEvidence(),
            ["snapshots"] = entries
        };
        File.WriteAllText(manifestPath, manifest.ToJsonString(JsonOptions));
        return manifestPath;
    }

    private string WriteEmptySnapshotSet(string outputDirectory, UiSnapshotRequest request)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "ui-snapshot-manifest.json");
        File.WriteAllText(path, new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["surface"] = request.Surface,
            ["theme"] = request.Theme,
            ["density"] = request.Density,
            ["size"] = request.Size,
            ["snapshotCount"] = 0,
            ["pixelSnapshotCount"] = 0,
            ["snapshots"] = new JsonArray()
        }.ToJsonString(JsonOptions));
        return path;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static bool MatchesSurfaceFilter(MptUiSurfaceManifest surface, string filter)
    {
        return string.IsNullOrWhiteSpace(filter) ||
            filter == "*" ||
            string.Equals(surface.Kind, filter, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(surface.SurfaceId, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' ? ch : '-');
        }

        return builder.ToString();
    }

    private static readonly string[] RequiredShellSurfaceIds =
    [
        "shell.dashboard",
        "shell.command-palette",
        "shell.settings-center",
        "shell.module-detail",
        "shell.logs-viewer",
        "shell.notification-center",
        "shell.permission-prompt",
        "shell.degraded-module"
    ];

    private static IReadOnlyList<MptUiSurfaceManifest> CreateShellSurfaces()
    {
        return
        [
            ShellSurface("shell.dashboard", "dashboard", ["MptDashboardCard", "MptMetricGrid", "MptStatusPill", "MptCommandButton"], ["loading", "ready", "degraded", "error"]),
            ShellSurface("shell.command-palette", "command-palette", ["MptSearchBox", "MptCommandItem", "MptStatusPill", "MptEmptyState"], ["loading", "ready", "empty", "permission-required", "validation-error", "executing", "succeeded", "error"]),
            ShellSurface("shell.settings-center", "settings-center", ["MptSettingsSection", "MptSettingRow", "MptActionBar", "MptErrorView"], ["loading", "ready", "staged-diff", "apply-failed", "validation-error", "conflict", "error"]),
            ShellSurface("shell.module-detail", "module-detail", ["MptModuleHeader", "MptMetricGrid", "MptDiagnosticPanel", "MptActionBar"], ["loading", "ready", "degraded", "error"]),
            ShellSurface("shell.logs-viewer", "logs-viewer", ["MptLogViewer", "MptSearchBox", "MptEmptyState", "MptErrorView"], ["loading", "ready", "streaming", "empty", "error"]),
            ShellSurface("shell.notification-center", "notification-center", ["MptTimeline", "MptStatusPill", "MptEmptyState", "MptActionBar"], ["loading", "ready", "empty", "error"]),
            ShellSurface("shell.permission-prompt", "permission-prompt", ["MptPermissionPrompt", "MptDiagnosticPanel", "MptActionBar", "MptStatusPill"], ["loading", "ready", "permission-required", "error"]),
            ShellSurface("shell.degraded-module", "degraded-module", ["MptModuleHeader", "MptDiagnosticPanel", "MptErrorView", "MptActionBar"], ["loading", "degraded", "error"]),
            ShellSurface("shell.package-manager", "package-manager", ["MptDataTable", "MptStatusPill", "MptActionBar", "MptEmptyState"], ["loading", "ready", "degraded", "error"]),
            ShellSurface("shell.runtime-diagnostics", "runtime-diagnostics", ["MptMetricGrid", "MptDiagnosticPanel", "MptDataTable", "MptTimeline"], ["loading", "ready", "degraded", "error"])
        ];
    }

    private static MptUiSurfaceManifest ShellSurface(string surfaceId, string kind, List<string> uses, List<string> states)
    {
        return new MptUiSurfaceManifest
        {
            SchemaVersion = "1.0",
            SurfaceId = surfaceId,
            ModuleId = "shell",
            Kind = kind,
            Layout = new MptUiSurfaceLayout
            {
                Mode = "shell-page",
                PreferredWidth = 1366,
                PreferredHeight = 768,
                MinWidth = 960,
                MinHeight = 640
            },
            Uses = uses,
            States = states
        };
    }

    private static JsonObject CreateShellKeyboardNavigationEvidence()
    {
        return new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["shortcuts"] = new JsonArray
            {
                Shortcut("Ctrl+K", "focus-command-palette", "shell.command-palette"),
                Shortcut("Ctrl+F", "focus-command-palette", "shell.command-palette"),
                Shortcut("Escape", "clear-command-palette", "shell.command-palette"),
                Shortcut("F5", "refresh-current-page", "shell.dashboard"),
                Shortcut("Ctrl+R", "refresh-current-page", "shell.dashboard"),
                Shortcut("Ctrl+1", "navigate-dashboard", "shell.dashboard"),
                Shortcut("Ctrl+2", "navigate-modules", "shell.module-detail"),
                Shortcut("Ctrl+3", "navigate-settings", "shell.settings-center"),
                Shortcut("Ctrl+4", "navigate-logs", "shell.logs-viewer"),
                Shortcut("Ctrl+5", "navigate-notifications", "shell.notification-center"),
                Shortcut("Ctrl+6", "navigate-packages", "shell.package-manager"),
                Shortcut("Ctrl+7", "navigate-diagnostics", "shell.runtime-diagnostics")
            },
            ["focusStates"] = new JsonArray
            {
                "navigation-focus-visible",
                "command-search-focus-visible",
                "command-item-focus-visible",
                "content-action-focus-visible",
                "permission-audit-action-focus-visible",
                "package-operation-focus-visible",
                "diagnostics-process-action-focus-visible"
            }
        };
    }

    private static JsonObject Shortcut(string keys, string action, string surfaceId)
    {
        return new JsonObject
        {
            ["keys"] = keys,
            ["action"] = action,
            ["surfaceId"] = surfaceId
        };
    }

    private static IReadOnlyList<string> ShellKeyboardShortcutsFor(string surfaceId)
    {
        return surfaceId switch
        {
            "shell.dashboard" => ["F5", "Ctrl+R", "Ctrl+1"],
            "shell.command-palette" => ["Ctrl+K", "Ctrl+F", "Escape"],
            "shell.settings-center" => ["Ctrl+3"],
            "shell.module-detail" => ["Ctrl+2"],
            "shell.logs-viewer" => ["Ctrl+4"],
            "shell.notification-center" => ["Ctrl+5"],
            "shell.permission-prompt" => ["Ctrl+K", "Escape"],
            "shell.degraded-module" => ["Ctrl+2", "F5"],
            "shell.package-manager" => ["Ctrl+6"],
            "shell.runtime-diagnostics" => ["Ctrl+7", "F5"],
            _ => []
        };
    }

    private static IReadOnlyList<string> ShellFocusStatesFor(string surfaceId)
    {
        return surfaceId switch
        {
            "shell.dashboard" => ["navigation-focus-visible", "dashboard-card-action-focus-visible", "refresh-focus-visible"],
            "shell.command-palette" => ["command-search-focus-visible", "command-item-focus-visible", "command-parameter-validation-readable", "command-result-readable", "empty-state-readable"],
            "shell.settings-center" => ["module-picker-focus-visible", "settings-editor-focus-visible", "settings-staged-diff-readable", "patch-preview-readable", "save-action-focus-visible"],
            "shell.module-detail" => ["module-action-focus-visible", "permission-section-readable", "diagnostic-card-focus-visible"],
            "shell.logs-viewer" => ["module-picker-focus-visible", "log-list-focus-visible", "empty-state-readable"],
            "shell.notification-center" => ["notification-item-focus-visible", "empty-state-readable"],
            "shell.permission-prompt" => ["permission-summary-readable", "audit-action-focus-visible", "rollback-details-readable"],
            "shell.degraded-module" => ["degraded-diagnostic-readable", "retry-action-focus-visible"],
            "shell.package-manager" => ["package-input-focus-visible", "package-action-focus-visible", "trust-badge-readable"],
            "shell.runtime-diagnostics" => ["process-action-focus-visible", "policy-history-focus-visible", "metric-tile-readable"],
            _ => []
        };
    }
}

public sealed record UiSnapshotRequest(string Surface, string Theme, string Size, string Density);
