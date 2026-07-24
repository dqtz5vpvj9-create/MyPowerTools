using MyPowerTools.Packaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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

    public IReadOnlyList<ValidationIssue> CheckShellSource(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var issues = new List<ValidationIssue>();
        var shellRoot = Path.Combine(root, "src", "MyPowerTools.Shell.Avalonia");
        var uiRoot = Path.Combine(root, "src", "MyPowerTools.UI");
        if (!Directory.Exists(shellRoot) || !Directory.Exists(uiRoot))
        {
            return issues;
        }

        var axamlFiles = Directory
            .EnumerateFiles(Path.Combine(shellRoot, "Views"), "*.axaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(uiRoot, "Controls"), "*.axaml", SearchOption.AllDirectories));
        foreach (var file in axamlFiles)
        {
            var text = File.ReadAllText(file);
            AddIfMatches(issues, file, text, "#[0-9A-Fa-f]{3,8}|Brush\\.Parse|Brushes\\.", "MPTUI001 raw hex color outside token files. Use theme brush resources instead of raw colors.");
            AddIfMatches(issues, file, text, "\\b(Margin|Padding|Spacing)=\"[0-9]", "MPTUI002 raw spacing outside token files. Use spacing resources instead of raw spacing literals.");
            AddIfMatches(issues, file, text, "\\bFontSize=\"[0-9]", "MPTUI003 raw typography outside token files. Use typography resources instead of raw FontSize literals.");
        }

        var csharpFiles = Directory
            .EnumerateFiles(shellRoot, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(uiRoot, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        foreach (var file in csharpFiles)
        {
            var text = File.ReadAllText(file);
            AddIfMatches(issues, file, text, "Brush\\.Parse\\s*\\(", "MPTUI001 raw hex color outside token files. Use theme brush resources instead of Brush.Parse.");
            AddIfMatches(issues, file, text, "\\bBrushes\\.[A-Za-z]", "MPTUI001 raw hex color outside token files. Use theme brush resources instead of raw Brushes.");

            if (!IsTokenFile(file))
            {
                AddIfMatches(issues, file, text, "new\\s+Thickness\\s*\\(\\s*[0-9]", "MPTUI002 raw spacing outside token files. Use spacing tokens instead of raw Thickness literals.");
                AddIfMatches(issues, file, text, "new\\s+CornerRadius\\s*\\(\\s*[0-9]", "MPTUI002 raw spacing outside token files. Use radius tokens instead of raw CornerRadius literals.");
                AddIfMatches(issues, file, text, "\\bFontSize\\w*\\s*(?:=|=>)\\s*[0-9]", "MPTUI003 raw typography outside token files. Use typography tokens instead of raw FontSize values.");
            }
        }

        AddShellSemanticRules(issues, shellRoot, uiRoot);
        return issues;
    }

    private static void AddShellSemanticRules(List<ValidationIssue> issues, string shellRoot, string uiRoot)
    {
        var shellChromePath = Path.Combine(shellRoot, "Views", "ShellChromeView.axaml");
        var dashboardPath = Path.Combine(shellRoot, "Views", "DashboardView.axaml");
        var commandPalettePath = Path.Combine(shellRoot, "Views", "CommandPaletteView.axaml");
        var mainWindowPath = Path.Combine(shellRoot, "MainWindow.cs");
        var shellProjectPath = Path.Combine(shellRoot, "MyPowerTools.Shell.Avalonia.csproj");

        if (File.Exists(mainWindowPath))
        {
            var mainWindowLines = File.ReadLines(mainWindowPath).Count();
            if (mainWindowLines > 120)
            {
                issues.Add(new ValidationIssue(mainWindowPath, "error", $"MPTUI006 MainWindow.cs over 120 lines. MainWindow has {mainWindowLines} lines."));
            }
        }

        foreach (var viewModelFile in Directory.EnumerateFiles(Path.Combine(shellRoot, "ViewModels"), "*.cs"))
        {
            var lineCount = File.ReadLines(viewModelFile).Count();
            if (lineCount > 350)
            {
                issues.Add(new ValidationIssue(viewModelFile, "error", $"MPTUI007 ViewModel over 350 lines. ViewModel file has {lineCount} lines."));
            }
        }

        foreach (var controllerFile in Directory.EnumerateFiles(Path.Combine(shellRoot, "Services"), "ShellWorkspaceController*.cs"))
        {
            var lineCount = File.ReadLines(controllerFile).Count();
            if (lineCount > 400)
            {
                issues.Add(new ValidationIssue(controllerFile, "error", $"MPTUI008 controller over 400 lines. Shell controller file has {lineCount} lines."));
            }
        }

        foreach (var viewPath in Directory.EnumerateFiles(Path.Combine(shellRoot, "Views"), "*.axaml", SearchOption.AllDirectories))
        {
            var view = File.ReadAllText(viewPath);
            var rawControl = Regex.Match(view, @"</?(Button|TextBox|ComboBox|CheckBox)\b");
            if (rawControl.Success)
            {
                issues.Add(new ValidationIssue(viewPath, "error", $"MPTUI004 raw Avalonia Button/TextBox in Shell pages. Use MPT controls instead of raw {rawControl.Groups[1].Value}."));
            }
        }

        foreach (var codeBehindPath in Directory.EnumerateFiles(Path.Combine(shellRoot, "Views"), "*.axaml.cs", SearchOption.AllDirectories))
        {
            var codeBehind = File.ReadAllText(codeBehindPath);
            AddIfMatches(
                issues,
                codeBehindPath,
                codeBehind,
                "new\\s+(Grid|Button|TextBox|ComboBox|CheckBox|StackPanel|Border|ScrollViewer|ContentControl)\\b",
                "MPTUI005 production page code-behind creates layout controls. Keep production Shell views in AXAML with thin code-behind.");
        }

        if (File.Exists(shellProjectPath))
        {
            var project = File.ReadAllText(shellProjectPath);
            AddIfMatches(issues, shellProjectPath, project, "AndroidTools|AdbForwarder|DoubaoAgent|ScreenEase|SmartBird", "MPTUI009 Shell references concrete module project. Shell must depend on HostControl contracts and shared UI only.");
        }

        if (File.Exists(shellChromePath))
        {
            var chrome = File.ReadAllText(shellChromePath);
            if (!chrome.Contains("GlobalOverlayHost", StringComparison.Ordinal) ||
                !chrome.Contains("IsCommandPaletteOpen", StringComparison.Ordinal))
            {
                issues.Add(new ValidationIssue(shellChromePath, "error", "MPTUI011 dashboard contains permanent command palette rail. ShellChrome must expose a global command overlay host."));
            }

            if (Regex.IsMatch(chrome, "ColumnDefinitions\\s*=\\s*\"[^\"]*,\\*,\\s*(?:3[0-9]{2}|[0-9]{3})\"") ||
                Regex.IsMatch(chrome, "Grid\\.Column\\s*=\\s*\"2\"[\\s\\S]{0,240}CommandPanel"))
            {
                issues.Add(new ValidationIssue(shellChromePath, "error", "MPTUI011 dashboard contains permanent command palette rail. Command Palette must be global overlay content."));
            }

            if (Regex.IsMatch(chrome, "Text\\s*=\\s*\"MyPowerTools\"[\\s\\S]{0,120}NavigationItems"))
            {
                issues.Add(new ValidationIssue(shellChromePath, "error", "MPTUI012 duplicate page heading. Sidebar brand and page title must not duplicate the same heading."));
            }

            if (chrome.Contains("sample-fixture", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ValidationIssue(shellChromePath, "error", "MPTUI010 sample-fixture text appears in production screenshot source. Fixture labels belong only in manifests."));
            }
        }

        if (File.Exists(dashboardPath))
        {
            var dashboard = File.ReadAllText(dashboardPath);
            if (!dashboard.Contains("MptStatusBadge", StringComparison.Ordinal))
            {
                issues.Add(new ValidationIssue(dashboardPath, "error", "MPTUI014 status without StatusBadge. Dashboard status text must render through MptStatusBadge."));
            }

            foreach (var required in new[] { "MptMetricTile", "MptModuleCard", "MptPrimaryButton" })
            {
                if (!dashboard.Contains(required, StringComparison.Ordinal))
                {
                    issues.Add(new ValidationIssue(dashboardPath, "error", $"MPTUI015 card without primary action or details action. Dashboard is missing required component class {required}."));
                }
            }

            if (dashboard.Contains("Command Palette", StringComparison.Ordinal) ||
                dashboard.Contains("BrokerAuditView", StringComparison.Ordinal))
            {
                issues.Add(new ValidationIssue(dashboardPath, "error", "MPTUI011 dashboard contains permanent command palette rail. Dashboard must not embed Command Palette or broker audit panels."));
            }
        }

        if (File.Exists(commandPalettePath))
        {
            var commandPalette = File.ReadAllText(commandPalettePath);
            if (!Regex.IsMatch(commandPalette, "Text\\s*=\\s*\"\\{Binding Label\\}\"[\\s\\S]{0,240}<controls:MptTextBox"))
            {
                issues.Add(new ValidationIssue(commandPalettePath, "error", "MPTUI013 command parameter field without label. Command parameters must show a label before editable input."));
            }
        }

        var themeDirectory = Path.Combine(uiRoot, "Themes");
        foreach (var requiredTheme in new[]
        {
            "MptTheme.axaml",
            "MptColors.axaml",
            "MptTypography.axaml",
            "MptSpacing.axaml",
            "MptRadii.axaml",
            "MptShadows.axaml",
            "MptDensity.axaml",
            "MptAnimations.axaml"
        })
        {
            var path = Path.Combine(themeDirectory, requiredTheme);
            if (!File.Exists(path))
            {
                issues.Add(new ValidationIssue(path, "error", $"MPTUI016 Required theme token file {requiredTheme} is missing."));
            }
        }

        var controlsDirectory = Path.Combine(uiRoot, "Controls");
        foreach (var requiredControl in RequiredControlStyleFiles)
        {
            var path = Path.Combine(controlsDirectory, requiredControl);
            if (!File.Exists(path))
            {
                issues.Add(new ValidationIssue(path, "error", $"MPTUI017 Required Shell component style file {requiredControl} is missing."));
            }
        }
    }

    private static readonly string[] RequiredControlStyleFiles =
    [
        "MptButton.axaml",
        "MptIconButton.axaml",
        "MptSidebar.axaml",
        "MptTopBar.axaml",
        "MptSearchBox.axaml",
        "MptModuleCard.axaml",
        "MptStatusBadge.axaml",
        "MptMetricTile.axaml",
        "MptCommandPalette.axaml",
        "MptCommandListItem.axaml",
        "MptCommandParameterForm.axaml",
        "MptSettingsSection.axaml",
        "MptSettingsField.axaml",
        "MptLogViewer.axaml",
        "MptNotificationItem.axaml",
        "MptPackageCard.axaml",
        "MptPermissionPrompt.axaml",
        "MptEmptyState.axaml",
        "MptErrorState.axaml",
        "MptLoadingSkeleton.axaml",
        "MptPageHeader.axaml",
        "MptToolbar.axaml",
        "MptTabStrip.axaml",
        "MptSplitView.axaml"
    ];

    private static bool IsTokenFile(string file)
    {
        var name = Path.GetFileName(file);
        return name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
            file.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
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
                    var snapshotBaseName = $"{Sanitize(surface.SurfaceId)}.{Sanitize(request.Theme)}.{Sanitize(request.Density)}.{Sanitize(request.Size)}.contract";
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
                ["artifactKind"] = "contract",
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
            var snapshotBaseName = $"{Sanitize(surface.SurfaceId)}.{Sanitize(request.Theme)}.{Sanitize(request.Density)}.{Sanitize(request.Size)}.contract";
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
            ["artifactKind"] = "contract",
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

    private static void AddIfMatches(List<ValidationIssue> issues, string file, string text, string pattern, string message)
    {
        if (Regex.IsMatch(text, pattern))
        {
            issues.Add(new ValidationIssue(file, "error", message));
        }
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
        "shell.package-manager",
        "shell.runtime-diagnostics"
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
                Shortcut("Ctrl+Alt+Space", "focus-command-palette", "shell.command-palette"),
                Shortcut("Ctrl+Shift+P", "focus-command-palette", "shell.command-palette"),
                Shortcut("Escape", "clear-command-palette", "shell.command-palette"),
                Shortcut("F5", "refresh-current-page", "shell.dashboard"),
                Shortcut("Ctrl+R", "refresh-current-page", "shell.dashboard"),
                Shortcut("Ctrl+1", "navigate-dashboard", "shell.dashboard"),
                Shortcut("Ctrl+2", "navigate-modules", "shell.module-detail"),
                Shortcut("Ctrl+3", "navigate-commands", "shell.command-palette"),
                Shortcut("Ctrl+4", "navigate-settings", "shell.settings-center"),
                Shortcut("Ctrl+5", "navigate-logs", "shell.logs-viewer"),
                Shortcut("Ctrl+6", "navigate-notifications", "shell.notification-center"),
                Shortcut("Ctrl+7", "navigate-packages", "shell.package-manager"),
                Shortcut("Ctrl+8", "navigate-diagnostics", "shell.runtime-diagnostics")
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
            "shell.command-palette" => ["Ctrl+Alt+Space", "Ctrl+Shift+P", "Ctrl+3", "Escape"],
            "shell.settings-center" => ["Ctrl+4"],
            "shell.module-detail" => ["Ctrl+2"],
            "shell.logs-viewer" => ["Ctrl+5"],
            "shell.notification-center" => ["Ctrl+6"],
            "shell.permission-prompt" => ["Ctrl+Shift+P", "Escape"],
            "shell.degraded-module" => ["Ctrl+2", "F5"],
            "shell.package-manager" => ["Ctrl+7"],
            "shell.runtime-diagnostics" => ["Ctrl+8", "F5"],
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
