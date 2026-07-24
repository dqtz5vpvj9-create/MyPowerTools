using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MyPowerTools.Abstractions;
using MyPowerTools.Ipc;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using MyPowerTools.ServiceManager.Client;
using Grpc.Core;
using SM = MyPowerTools.Protocol.ServiceManager.V1;

// A3 Process Gate driver.
//
// Verifies the ServiceManager lifecycle model against a running ServiceManager process:
//   A3.1  admin ListUnits sees a registered unit
//   A3.2  scoped client Start -> admin observes active + PID via event
//   A3.3  repeated Start keeps the same PID (single instance)
//   A3.4  scoped client cannot see/control a unit owned by a different tool (SCOPE_DENIED)
//   A3.5  scoped Stop -> admin observes inactive
//
// The driver assumes the caller (verify-architecture.ps1) has already:
//   - launched MyPowerTools.ServiceManager with MPT_DATA_ROOT set to <dataRoot>
//   - deployed a unit manifest for <unitId> owned by <toolId> to the deploy root
//   - built the test-service-unit fixture and made its exec resolvable
//
// Output: a JSON result object on stdout; exit code 0 = pass, 1 = fail.

// Utility mode: gracefully shut down the ServiceManager via gRPC (used by verification scripts
// to test re-adoption without force-killing the process). Exits before the A3 flow.
var mode = GetOptionalArg("--mode");
if (string.Equals(mode, "shutdown", StringComparison.OrdinalIgnoreCase))
{
    var shutdownDataRoot = RequireArg("--data-root");
    Environment.SetEnvironmentVariable("MPT_DATA_ROOT", shutdownDataRoot);
    try
    {
        var shutdownEndpointAddress = GetOptionalArg("--endpoint-address");
        using var admin = string.IsNullOrWhiteSpace(shutdownEndpointAddress)
            ? ServiceManagerAdminClient.ForDefaultEndpoint()
            : ServiceManagerAdminClient.ForEndpoint(new IpcEndpoint(
                PlatformId.Current().OperatingSystem == "windows"
                    ? IpcTransport.NamedPipe
                    : IpcTransport.UnixDomainSocket,
                shutdownEndpointAddress));
        var ok = await admin.ShutdownAsync();
        Console.WriteLine($"shutdown requested: ok={ok}");
        return ok ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"shutdown failed: {ex.Message}");
        return 1;
    }
}

// A1 Quick Gate: structural dependency boundary scan. No process launch needed.
if (string.Equals(mode, "a1", StringComparison.OrdinalIgnoreCase))
{
    return RunA1Gate();
}

if (string.Equals(mode, "a3", StringComparison.OrdinalIgnoreCase))
{
    return await ProcessGateRunner.RunA3Async();
}

// A4 Process Gate: fault domain. A crashed unit enters failed/recoverable, the ServiceManager
// and other units continue, and the unit can be restarted.
if (string.Equals(mode, "a4", StringComparison.OrdinalIgnoreCase))
{
    return await ProcessGateRunner.RunA4Async();
}

// A2 Quick Gate: dynamic discovery and data autonomy. Verifies that a new tool directory
// appears in the catalog after refresh and disappears after removal, and that default uninstall
// preserves declared dataRoots while explicit purge removes them.
if (string.Equals(mode, "a2", StringComparison.OrdinalIgnoreCase))
{
    return await RunA2Gate();
}

static async Task<int> RunA2Gate()
{
    var repoRoot = FindRepoRoot();
    var startedAt = Stopwatch.StartNew();
    var records = new List<GateRecord>();
    var overall = true;
    var runId = Guid.NewGuid().ToString("N")[..8];
    var tempDir = Path.Combine(Path.GetTempPath(), $"mpt-a2-{runId}");
    var modulesRoot = Path.Combine(tempDir, "modules");
    var scanRoot = Path.Combine(tempDir, "external-tools");
    var toolDir = Path.Combine(scanRoot, "minimal-tool");
    var dataRoot = Path.Combine(tempDir, "tool-data");
    var storeRoot = Path.Combine(tempDir, "package-store");
    var evidenceDir = Path.Combine(repoRoot, "artifacts", "architecture-smoke", "a2");
    Directory.CreateDirectory(modulesRoot);
    Directory.CreateDirectory(scanRoot);
    Directory.CreateDirectory(dataRoot);
    Directory.CreateDirectory(evidenceDir);

    try
    {
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(tempDir, "runtime-data")));
        runtime.Load(modulesRoot, [scanRoot]);

        var before = runtime.ListTools(includeDisabled: true);
        var beforePath = Path.Combine(evidenceDir, $"catalog-before-{runId}.json");
        await WriteCatalogSnapshotAsync(beforePath, before);
        var startsEmpty = before.Count == 0;
        records.Add(new("A2.1-catalog-starts-empty", startsEmpty, $"count={before.Count}; evidence={beforePath}"));
        overall &= startsEmpty;

        Directory.CreateDirectory(toolDir);
        await File.WriteAllTextAsync(Path.Combine(toolDir, "index.html"), "<h1>A2 external tool</h1>");
        var toolId = $"architecture.a2.{runId}";
        var manifest = new
        {
            schemaVersion = "1.0",
            version = "0.1.0",
            toolId,
            ownerModuleId = toolId,
            title = "A2 External Tool",
            description = "Real external Tool Catalog fixture.",
            icon = "tool.test",
            category = "Tests",
            type = "web-surface",
            availability = "available",
            primaryRouteId = "main",
            routes = new[]
            {
                new
                {
                    routeId = "main",
                    surfaceId = $"{toolId}.main",
                    title = "Main",
                    surface = new { kind = "web", source = "index.html", openExternal = false }
                }
            },
            homeCard = new { summary = "A2 real catalog fixture", primaryActionLabel = "Open", order = 900 },
            commands = new[] { new { id = $"{toolId}.refresh", title = "Refresh", description = "Refresh fixture", method = "POST", path = "/refresh" } },
            dataRoots = new[] { dataRoot },
            dataRetention = "preserve",
            permissions = Array.Empty<object>()
        };
        await File.WriteAllTextAsync(
            Path.Combine(toolDir, "tool.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        var afterAdd = await runtime.RefreshToolCatalogAsync(CancellationToken.None);
        var afterAddPath = Path.Combine(evidenceDir, $"catalog-after-add-{runId}.json");
        await WriteCatalogSnapshotAsync(afterAddPath, afterAdd);
        var discovered = afterAdd.SingleOrDefault(tool => string.Equals(tool.Descriptor.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
        var discoveryOk = discovered is not null &&
                          discovered.Descriptor.ToolType == "web-surface" &&
                          discovered.Descriptor.Commands?.Any(command => command.Id == $"{toolId}.refresh") == true &&
                          discovered.Descriptor.DataRoots?.SequenceEqual([Path.GetFullPath(dataRoot)], StringComparer.OrdinalIgnoreCase) == true;
        records.Add(new("A2.2-real-refresh-discovers-tool", discoveryOk,
            $"tool={discovered?.Descriptor.ToolId ?? "missing"}; type={discovered?.Descriptor.ToolType ?? "missing"}; evidence={afterAddPath}"));
        overall &= discoveryOk;

        Directory.Delete(toolDir, recursive: true);
        var afterRemove = await runtime.RefreshToolCatalogAsync(CancellationToken.None);
        var afterRemovePath = Path.Combine(evidenceDir, $"catalog-after-remove-{runId}.json");
        await WriteCatalogSnapshotAsync(afterRemovePath, afterRemove);
        var removed = afterRemove.All(tool => !string.Equals(tool.Descriptor.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
        records.Add(new("A2.3-real-refresh-removes-tool", removed, $"count={afterRemove.Count}; evidence={afterRemovePath}"));
        overall &= removed;

        var sentinelFile = Path.Combine(dataRoot, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelFile, "A2 data autonomy sentinel");
        var store = new PackageStore(storeRoot, Path.Combine(repoRoot, "schemas"));
        var fixturePackage = Path.Combine(repoRoot, "tests", "fixtures", "modules", "sample-dotnet");
        var installed = store.Install(fixturePackage);
        var defaultUninstall = installed.Success
            ? store.Uninstall("sample-dotnet", [dataRoot], purgeData: false)
            : installed;
        var preserved = defaultUninstall.Success && File.Exists(sentinelFile);
        records.Add(new("A2.4-default-uninstall-preserves-data", preserved,
            $"install={installed.Success}; uninstall={defaultUninstall.Success}; sentinel={File.Exists(sentinelFile)}"));
        overall &= preserved;

        var rolledBack = defaultUninstall.Success && store.Rollback("sample-dotnet").Success;
        var purgeUninstall = rolledBack
            ? store.Uninstall("sample-dotnet", [dataRoot], purgeData: true)
            : new PackageInstallResult(false, "sample-dotnet", "", [new ValidationIssue(dataRoot, "error", "Rollback failed before purge test.")]);
        var purged = purgeUninstall.Success && !Directory.Exists(dataRoot);
        records.Add(new("A2.5-explicit-purge-removes-data", purged,
            $"rollback={rolledBack}; uninstall={purgeUninstall.Success}; dataRootExists={Directory.Exists(dataRoot)}"));
        overall &= purged;
    }
    catch (Exception ex)
    {
        records.Add(new("A2.exception", false, $"{ex.GetType().Name}: {ex.Message}"));
        overall = false;
    }
    finally
    {
        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }

    foreach (var r in records)
    {
        Console.WriteLine($"[{(r.Passed ? "PASS" : "FAIL")}] {r.Id}: {r.Detail}");
    }

    Console.WriteLine($"A2 gate: {(overall ? "PASS" : "FAIL")} ({startedAt.ElapsedMilliseconds} ms)");
    return overall ? 0 : 1;
}

static async Task WriteCatalogSnapshotAsync(string path, IReadOnlyList<RuntimeToolSnapshot> tools)
{
    var payload = tools.Select(tool => new
    {
        tool.Descriptor.ToolId,
        tool.Descriptor.ToolType,
        tool.Descriptor.SourceDirectory,
        Routes = tool.Descriptor.Routes.Select(route => new { route.RouteId, route.SurfaceKind, route.Source }),
        Commands = tool.Descriptor.Commands?.Select(command => command.Id) ?? [],
        DataRoots = tool.Descriptor.DataRoots ?? []
    });
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
}

static int RunA1Gate()
{
    var repoRoot = FindRepoRoot();
    var stopwatch = Stopwatch.StartNew();
    var records = new List<GateRecord>();
    var overall = true;

    var rulePath = Path.Combine(repoRoot, "tests", "architecture-rules.json");
    var rulesParse = JsonNode.Parse(File.ReadAllText(rulePath)) is JsonObject;
    records.Add(new("A1.1-rules-load", rulesParse, $"rules={rulePath}"));
    overall &= rulesParse;

    var shellCsproj = Path.Combine(repoRoot, "src", "MyPowerTools.Shell.Avalonia", "MyPowerTools.Shell.Avalonia.csproj");
    var shellItems = ReadProjectItems(shellCsproj);
    var toolLinks = shellItems
        .Where(item => item.ItemType is "Compile" or "ProjectReference" && ContainsToolsPath(item.Include))
        .ToArray();
    var noToolLinks = toolLinks.Length == 0;
    records.Add(new("A1.2-shell-no-tool-source-or-project-link", noToolLinks,
        noToolLinks ? "zero tools/** Compile/ProjectReference items" : string.Join("; ", toolLinks.Select(item => $"{item.ItemType}:{item.Include}"))));
    overall &= noToolLinks;

    var shellForbiddenRefs = FindForbiddenProjectReferences(
        shellItems,
        ["AdbForwarder.Surface", "ScreenEase.Surface", "RemoteNotifications.Surface", "DoubaoAgent.Surface", "SmartBird.Surface", "MyPowerTools.HostControl", "MyPowerTools.UI.Testing"]);
    var shellDepsClean = shellForbiddenRefs.Length == 0;
    records.Add(new("A1.3-shell-production-dependencies", shellDepsClean,
        shellDepsClean ? "Shell references client/production assemblies only" : $"forbidden={string.Join(", ", shellForbiddenRefs)}"));
    overall &= shellDepsClean;

    var cliCsproj = Path.Combine(repoRoot, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj");
    var cliForbiddenRefs = FindForbiddenProjectReferences(ReadProjectItems(cliCsproj), ["MyPowerTools.Shell.Avalonia", "MyPowerTools.UI.Testing"]);
    var cliClean = cliForbiddenRefs.Length == 0;
    records.Add(new("A1.4-cli-no-shell-or-visual-harness", cliClean,
        cliClean ? "product CLI is independent from Shell/visual testing" : $"forbidden={string.Join(", ", cliForbiddenRefs)}"));
    overall &= cliClean;

    var clientItems = ReadProjectItems(Path.Combine(repoRoot, "src", "MyPowerTools.HostControl.Client", "MyPowerTools.HostControl.Client.csproj"));
    var clientForbidden = FindForbiddenProjectReferences(clientItems, ["MyPowerTools.Runtime", "MyPowerTools.HostControl.Server"]);
    var clientClean = clientForbidden.Length == 0;
    records.Add(new("A1.5-hostcontrol-client-direction", clientClean,
        clientClean ? "client has no Runtime/Server dependency" : $"forbidden={string.Join(", ", clientForbidden)}"));
    overall &= clientClean;

    var primitiveItems = ReadProjectItems(Path.Combine(repoRoot, "src", "MyPowerTools.UI.Primitives", "MyPowerTools.UI.Primitives.csproj"));
    var primitiveForbidden = FindForbiddenProjectReferences(primitiveItems, ["MyPowerTools.Runtime", "MyPowerTools.Packaging", "MyPowerTools.Broker", "MyPowerTools.Shell.Avalonia"]);
    var primitivesClean = primitiveForbidden.Length == 0;
    records.Add(new("A1.6-ui-primitives-direction", primitivesClean,
        primitivesClean ? "UI.Primitives has no upper-layer dependency" : $"forbidden={string.Join(", ", primitiveForbidden)}"));
    overall &= primitivesClean;

    var toolCsprojFiles = Directory.EnumerateFiles(Path.Combine(repoRoot, "tools"), "*.csproj", SearchOption.AllDirectories)
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}original-source{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        .Where(path => !Path.GetFileNameWithoutExtension(path).Contains("Tests", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var parentSourceReferences = toolCsprojFiles
        .SelectMany(path => ReadProjectItems(path)
            .Where(item => item.ItemType == "ProjectReference" && item.Include.Contains("$(MyPowerToolsRepoRoot)\\src\\", StringComparison.OrdinalIgnoreCase))
            .Select(item => $"{Path.GetRelativePath(repoRoot, path)} -> {item.Include}"))
        .ToArray();
    var externalBoundaryClean = parentSourceReferences.Length == 0;
    records.Add(new("A1.7-tools-use-packages", externalBoundaryClean,
        externalBoundaryClean ? $"scanned {toolCsprojFiles.Length} tool projects; zero parent src references" : string.Join("; ", parentSourceReferences)));
    overall &= externalBoundaryClean;

    var controllerFiles = Directory.GetFiles(
        Path.Combine(repoRoot, "src", "MyPowerTools.Shell.Avalonia", "Services"),
        "ShellWorkspaceController*.cs",
        SearchOption.TopDirectoryOnly);
    var forbiddenIds = new[]
    {
        "RemoteNotificationsToolId",
        "AdbForwarderToolId",
        "ScreenEaseToolId",
        "DoubaoAgentToolId",
        "SmartBirdThermostatToolId",
        "DeliveredToolIds",
        "\"adb-forwarder\"",
        "\"remote-notifications\"",
        "\"android-tools.notifications\"",
        "\"screenease\"",
        "\"doubao-agent\"",
        "\"smartbird-thermostat\""
    };
    var foundIds = new List<string>();
    foreach (var file in controllerFiles)
    {
        if (!File.Exists(file)) continue;
        var content = File.ReadAllText(file);
        foreach (var id in forbiddenIds)
        {
            if (content.Contains(id, StringComparison.Ordinal))
            {
                foundIds.Add(id);
            }
        }
    }
    var noToolIds = foundIds.Count == 0;
    records.Add(new("A1.8-shell-no-tool-ids", noToolIds,
        noToolIds ? $"No first-party tool IDs across {controllerFiles.Length} ShellWorkspaceController partials" : $"Found forbidden identifiers: {string.Join(", ", foundIds)}"));
    overall &= noToolIds;

    var sdkCsproj = Path.Combine(repoRoot, "src", "MyPowerTools.AvaloniaSdk", "MyPowerTools.AvaloniaSdk.csproj");
    var sdkViolations = FindForbiddenProjectReferences(ReadProjectItems(sdkCsproj), ["MyPowerTools.Runtime", "MyPowerTools.Packaging", "MyPowerTools.Broker", "MyPowerTools.Shell"]);
    var sdkClean = sdkViolations.Length == 0;
    records.Add(new("A1.9-sdk-no-upper-deps", sdkClean,
        sdkClean ? "AvaloniaSdk has no upper-layer dependencies" : $"SDK references forbidden: {string.Join(", ", sdkViolations)}"));
    overall &= sdkClean;

    var writerInShell = File.Exists(Path.Combine(repoRoot, "src", "MyPowerTools.Shell.Avalonia", "ShellRealScreenshotWriter.cs"));
    var writerInVisualProject = File.Exists(Path.Combine(repoRoot, "src", "Mpt.Cli.VisualTesting", "ShellRealScreenshotWriter.cs"));
    var visualSplit = !writerInShell && writerInVisualProject;
    records.Add(new("A1.10-screenshot-writer-outside-product-shell", visualSplit,
        $"inShell={writerInShell}; inVisualTesting={writerInVisualProject}"));
    overall &= visualSplit;

    var firstPartyToolManifests = new[]
    {
        "tools/adb-forwarder/current-integration/modules/adb-forwarder/ui/tool.json",
        "tools/doubao-computer-use/current-integration/modules/doubao-agent/ui/tool.json",
        "tools/remote-notifications/current-integration/modules/android-tools-suite/modules/notifications/ui/tool.json",
        "tools/screenease/current-integration/modules/screenease/ui/tool.json",
        "tools/smartbird-thermostat/current-integration/modules/smartbird-thermostat/ui/tool.json"
    };
    var surfaceManifestErrors = new List<string>();
    foreach (var relativePath in firstPartyToolManifests)
    {
        var manifestPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (JsonNode.Parse(File.ReadAllText(manifestPath)) is not JsonObject manifest ||
            !string.Equals(manifest["type"]?.GetValue<string>(), "dotnet-surface", StringComparison.Ordinal))
        {
            surfaceManifestErrors.Add($"{relativePath}:type");
            continue;
        }

        if (manifest["routes"] is not JsonArray routes || routes.Count == 0)
        {
            surfaceManifestErrors.Add($"{relativePath}:routes");
            continue;
        }

        foreach (var route in routes.OfType<JsonObject>())
        {
            if (route["surface"] is not JsonObject surface ||
                !string.Equals(surface["kind"]?.GetValue<string>(), "dotnet", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(surface["assembly"]?.GetValue<string>()) ||
                string.IsNullOrWhiteSpace(surface["type"]?.GetValue<string>()))
            {
                surfaceManifestErrors.Add($"{relativePath}:{route["routeId"]?.GetValue<string>() ?? "route"}");
            }
        }
    }
    var surfaceManifestsReady = surfaceManifestErrors.Count == 0;
    records.Add(new("A1.11-first-party-surface-contracts", surfaceManifestsReady,
        surfaceManifestsReady
            ? $"{firstPartyToolManifests.Length} first-party tools declare loadable dotnet surfaces"
            : string.Join("; ", surfaceManifestErrors)));
    overall &= surfaceManifestsReady;

    var serviceIntegrations = new[]
    {
        new
        {
            ToolManifest = "tools/remote-notifications/current-integration/modules/android-tools-suite/modules/notifications/ui/tool.json",
            UnitManifest = "tools/remote-notifications/current-integration/src/RemoteNotifications.Service/unit-manifest.json",
            SurfaceFactory = "tools/remote-notifications/current-integration/src/RemoteNotifications.Surface/RemoteNotificationsSurfaceFactory.cs"
        },
        new
        {
            ToolManifest = "tools/doubao-computer-use/current-integration/modules/doubao-agent/ui/tool.json",
            UnitManifest = "tools/doubao-computer-use/current-integration/src/DoubaoAgent.Controller.Service/unit-manifest.json",
            SurfaceFactory = "tools/doubao-computer-use/current-integration/src/DoubaoAgent.Surface/DoubaoAgentSurfaceFactory.cs"
        },
        new
        {
            ToolManifest = "tools/screenease/current-integration/modules/screenease/ui/tool.json",
            UnitManifest = "tools/screenease/current-integration/src/ScreenEase.Service/unit-manifest.json",
            SurfaceFactory = "tools/screenease/current-integration/src/ScreenEase.Surface/ScreenEaseSurfaceFactory.cs"
        }
    };
    var serviceIntegrationErrors = new List<string>();
    foreach (var integration in serviceIntegrations)
    {
        var toolManifestPath = Path.Combine(repoRoot, integration.ToolManifest.Replace('/', Path.DirectorySeparatorChar));
        var unitManifestPath = Path.Combine(repoRoot, integration.UnitManifest.Replace('/', Path.DirectorySeparatorChar));
        var surfaceFactoryPath = Path.Combine(repoRoot, integration.SurfaceFactory.Replace('/', Path.DirectorySeparatorChar));
        var toolManifest = JsonNode.Parse(File.ReadAllText(toolManifestPath)) as JsonObject;
        var unitManifest = JsonNode.Parse(File.ReadAllText(unitManifestPath)) as JsonObject;
        var toolId = toolManifest?["toolId"]?.GetValue<string>();
        var unitToolId = unitManifest?["toolId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(toolId) || !string.Equals(toolId, unitToolId, StringComparison.Ordinal))
        {
            serviceIntegrationErrors.Add($"{integration.UnitManifest}:toolId={unitToolId ?? "missing"}; expected={toolId ?? "missing"}");
        }
        if (!File.ReadAllText(surfaceFactoryPath).Contains("context.ServiceUnits", StringComparison.Ordinal))
        {
            serviceIntegrationErrors.Add($"{integration.SurfaceFactory}:scoped-client-missing");
        }
    }
    var serviceIntegrationsReady = serviceIntegrationErrors.Count == 0;
    records.Add(new("A1.12-first-party-service-unit-wiring", serviceIntegrationsReady,
        serviceIntegrationsReady
            ? $"{serviceIntegrations.Length} Service Unit tool scopes match their Surfaces"
            : string.Join("; ", serviceIntegrationErrors)));
    overall &= serviceIntegrationsReady;

    var webSurfaceImplementationPath = Path.Combine(
        repoRoot,
        "src",
        "MyPowerTools.WebSurface.Avalonia",
        "AvaloniaWebSurfaceService.cs");
    var webSurfaceImplementation = File.Exists(webSurfaceImplementationPath)
        ? File.ReadAllText(webSurfaceImplementationPath)
        : "";
    var forbiddenWebHostMarkers = new[]
    {
        "SmartBirdWebView",
        "NativeWebSurfaceCoordinator",
        "WebToolHostProtocol",
        "HostProcessEventKind",
        "MaximumHostFrameLength",
        "MyPowerTools.WebToolHost.exe"
    };
    var toolWebHostViolations = Directory.EnumerateFiles(Path.Combine(repoRoot, "tools"), "*.*", SearchOption.AllDirectories)
        .Where(path => Path.GetExtension(path) is ".cs" or ".csproj")
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}original-source{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        .SelectMany(path => forbiddenWebHostMarkers
            .Where(marker => File.ReadAllText(path).Contains(marker, StringComparison.Ordinal))
            .Select(marker => $"{Path.GetRelativePath(repoRoot, path)}:{marker}"))
        .ToArray();
    var toolHostReferences = toolCsprojFiles
        .SelectMany(path => FindForbiddenProjectReferences(ReadProjectItems(path), ["MyPowerTools.Shell.Avalonia", "MyPowerTools.WebToolHost"])
            .Select(reference => $"{Path.GetRelativePath(repoRoot, path)}:{reference}"))
        .ToArray();
    var webSurfaceBoundaryReady =
        webSurfaceImplementation.Contains("Process.Start(startInfo)", StringComparison.Ordinal) &&
        webSurfaceImplementation.Contains("--allowed-origin", StringComparison.Ordinal) &&
        toolWebHostViolations.Length == 0 &&
        toolHostReferences.Length == 0 &&
        !File.Exists(Path.Combine(repoRoot, "src", "MyPowerTools.Shell.Avalonia", "Views", "NativeWebSurfaceCoordinator.cs"));
    records.Add(new("A1.13-web-surface-host-boundary", webSurfaceBoundaryReady,
        webSurfaceBoundaryReady
            ? "SDK contract + single Shell-side WebToolHost client; tool projects contain no host protocol copies"
            : string.Join("; ", toolWebHostViolations.Concat(toolHostReferences))));
    overall &= webSurfaceBoundaryReady;

    var productionSources = EnumerateProductionCSharpFiles(repoRoot);
    var namedPipePolicyPath = Path.Combine(
        repoRoot,
        "src",
        "MyPowerTools.Ipc.Shared",
        "MptNamedPipePolicy.cs");
    var currentUserOnlyViolations = productionSources
        .Where(path => !string.Equals(path, namedPipePolicyPath, StringComparison.OrdinalIgnoreCase))
        .Where(path => File.ReadAllText(path).Contains("CurrentUserOnly", StringComparison.Ordinal))
        .Select(path => Path.GetRelativePath(repoRoot, path))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var currentUserOnlyRemoved = currentUserOnlyViolations.Length == 0;
    records.Add(new("A1.14-no-current-user-only-pipes", currentUserOnlyRemoved,
        currentUserOnlyRemoved
            ? $"scanned {productionSources.Length} production C# files"
            : string.Join("; ", currentUserOnlyViolations)));
    overall &= currentUserOnlyRemoved;

    var kestrelNamedPipeFiles = productionSources
        .Where(path => File.ReadAllText(path).Contains("ListenNamedPipe(", StringComparison.Ordinal))
        .ToArray();
    var kestrelPolicyViolations = kestrelNamedPipeFiles
        .Where(path => !ProjectConfiguresNamedPipePolicy(path, repoRoot))
        .Select(path => Path.GetRelativePath(repoRoot, path))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var kestrelPolicyReady = kestrelPolicyViolations.Length == 0;
    records.Add(new("A1.15-kestrel-named-pipes-use-shared-policy", kestrelPolicyReady,
        kestrelPolicyReady
            ? $"validated {kestrelNamedPipeFiles.Length} Kestrel named-pipe registrations"
            : string.Join("; ", kestrelPolicyViolations)));
    overall &= kestrelPolicyReady;

    foreach (var r in records)
    {
        Console.WriteLine($"[{(r.Passed ? "PASS" : "FAIL")}] {r.Id}: {r.Detail}");
    }

    Console.WriteLine($"A1 gate: {(overall ? "PASS" : "FAIL")} ({stopwatch.ElapsedMilliseconds} ms)");
    return overall ? 0 : 1;
}

static ProjectItem[] ReadProjectItems(string projectPath)
{
    var document = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
    return document.Descendants()
        .Where(element => element.Name.LocalName is "ProjectReference" or "Compile" or "Link")
        .Select(element => new ProjectItem(
            element.Name.LocalName,
            element.Attribute("Include")?.Value ?? element.Value))
        .Where(item => !string.IsNullOrWhiteSpace(item.Include))
        .ToArray();
}

static string[] FindForbiddenProjectReferences(IEnumerable<ProjectItem> items, IReadOnlyList<string> forbiddenProjectNames)
{
    return items
        .Where(item => item.ItemType == "ProjectReference")
        .Select(item => item.Include)
        .Where(include => forbiddenProjectNames.Any(name =>
        {
            var fileName = Path.GetFileNameWithoutExtension(include.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
            return string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase);
        }))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static bool ContainsToolsPath(string value)
{
    var normalized = value.Replace('\\', '/');
    return normalized.Contains("/tools/", StringComparison.OrdinalIgnoreCase) ||
           normalized.StartsWith("tools/", StringComparison.OrdinalIgnoreCase) ||
           normalized.Contains("../tools/", StringComparison.OrdinalIgnoreCase);
}

static string[] EnumerateProductionCSharpFiles(string repoRoot)
{
    var roots = new[]
    {
        Path.Combine(repoRoot, "src"),
        Path.Combine(repoRoot, "tools"),
        Path.Combine(repoRoot, "templates")
    };
    return roots
        .Where(Directory.Exists)
        .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        .Where(IsProductionSource)
        .ToArray();
}

static bool IsProductionSource(string path)
{
    var segments = Path.GetRelativePath(Directory.GetCurrentDirectory(), path)
        .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    return segments.All(segment =>
        !string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(segment, "test", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(segment, "fixtures", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(segment, "original-source", StringComparison.OrdinalIgnoreCase) &&
        !segment.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase));
}

static bool ProjectConfiguresNamedPipePolicy(string registrationFile, string repoRoot)
{
    var directory = new DirectoryInfo(Path.GetDirectoryName(registrationFile)!);
    while (directory is not null &&
           directory.FullName.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
    {
        if (directory.EnumerateFiles("*.csproj", SearchOption.TopDirectoryOnly).Any())
        {
            return directory
                .EnumerateFiles("*.cs", SearchOption.AllDirectories)
                .Where(path => IsProductionSource(path.FullName))
                .Select(path => File.ReadAllText(path.FullName))
                .Any(content =>
                    content.Contains("UseNamedPipes(", StringComparison.Ordinal) &&
                    content.Contains("MptNamedPipePolicy.Configure", StringComparison.Ordinal));
        }
        directory = directory.Parent;
    }
    return false;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "MyPowerTools.slnx")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}


var dataRoot = RequireArg("--data-root");
var unitId = RequireArg("--unit-id");
var toolId = RequireArg("--tool-id");
var otherToolId = RequireArg("--other-tool-id");
var resultPath = RequireArg("--result");

var records = new List<GateRecord>();
var overall = true;

Environment.SetEnvironmentVariable("MPT_DATA_ROOT", dataRoot);

try
{
    using var admin = ServiceManagerAdminClient.ForDefaultEndpoint();

    // A3.1 - the unit is registered and visible to administration.
    var listResp = await admin.ListUnitsAsync();
    var registered = listResp.Units.Any(u => u.UnitId == unitId);
    records.Add(new("A3.1-list-registered", registered, $"unit {unitId} visible, total={listResp.Units.Count}"));
    overall &= registered;

    var scoped = new ScopedServiceUnitClient(admin, toolId);

    // A3.2 - Start yields active + PID.
    var started = await scoped.StartAsync(unitId);
    var activeOk = started.State == ServiceUnitState.Active && (started.Pid ?? 0) > 0;
    records.Add(new("A3.2-scoped-start-active", activeOk, $"state={started.State} pid={started.Pid}"));
    overall &= activeOk;
    var firstPid = started.Pid ?? 0;

    // give the unit a moment to write its heartbeat
    await Task.Delay(800);

    // A3.3 - repeated Start is idempotent (same PID, single instance).
    var restarted = await scoped.StartAsync(unitId);
    var samePid = restarted.Pid == firstPid;
    records.Add(new("A3.3-idempotent-same-pid", samePid, $"first={firstPid} second={restarted.Pid}"));
    overall &= samePid;

    // A3.4 - scoped client for a DIFFERENT tool cannot see this unit.
    var otherScoped = new ScopedServiceUnitClient(admin, otherToolId);
    var otherList = await otherScoped.ListAsync();
    var cannotSee = !otherList.Any(u => u.Id == unitId);
    records.Add(new("A3.4-scope-isolation", cannotSee, $"other-tool sees {otherList.Count} units (expect 0 of {unitId})"));
    overall &= cannotSee;

    // A3.5 - scoped Stop -> admin observes inactive.
    var stopped = await scoped.StopAsync(unitId);
    var stoppedOk = stopped.State == ServiceUnitState.Inactive;
    records.Add(new("A3.5-scoped-stop-inactive", stoppedOk, $"state={stopped.State} pid={stopped.Pid}"));
    overall &= stoppedOk;

    // Confirm via administration client too.
    var adminSnap = await admin.GetUnitAsync(unitId);
    var adminConfirmed = adminSnap.State == SM.UnitState.Inactive;
    records.Add(new("A3.5b-admin-confirms-inactive", adminConfirmed, $"admin state={adminSnap.State}"));
    overall &= adminConfirmed;
}
catch (Exception ex)
{
    records.Add(new("driver-exception", false, $"{ex.GetType().Name}: {ex.Message}"));
    overall = false;
}

var result = new GateResult(
    Gate: "A3",
    Passed: overall,
    StartedAt: DateTimeOffset.UtcNow,
    DurationMs: 0,
    UnitId: unitId,
    ToolId: toolId,
    Records: records);

var dir = Path.GetDirectoryName(resultPath);
if (!string.IsNullOrEmpty(dir))
{
    Directory.CreateDirectory(dir);
}

await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

foreach (var r in records)
{
    Console.WriteLine($"[{(r.Passed ? "PASS" : "FAIL")}] {r.Id}: {r.Detail}");
}

Console.WriteLine($"A3 gate: {(overall ? "PASS" : "FAIL")}");
return overall ? 0 : 1;

static string RequireArg(string name)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    Console.Error.WriteLine($"Missing required argument {name}");
    Environment.Exit(2);
    return "";
}

static string? GetOptionalArg(string name)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

public sealed record GateResult(
    string Gate,
    bool Passed,
    DateTimeOffset StartedAt,
    long DurationMs,
    string UnitId,
    string ToolId,
    IReadOnlyList<GateRecord> Records);

public sealed record GateRecord(string Id, bool Passed, string Detail);
public sealed record ProjectItem(string ItemType, string Include);
