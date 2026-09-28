using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Json.Schema;

namespace RemoteToolGateway.Core.Tests;

/// <summary>
/// Packaging evidence: the template manifests point at the real assembly/type names, and the built
/// module carries the assemblies its collectible load context has to resolve (HostControl client,
/// protobuf, gRPC) instead of depending on a machine-global NuGet cache.
/// </summary>
public sealed class PackagingTests
{
    private static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "MyPowerTools.slnx"))) return directory;
            directory = Path.GetDirectoryName(directory);
        }

        throw new Xunit.Sdk.XunitException("Repository root was not found from " + AppContext.BaseDirectory);
    }

    private static string ToolRoot => Path.Combine(RepositoryRoot(), "tools", "remote-tool-gateway");

    private static string? ModuleOutputDirectory()
    {
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var candidate = Path.Combine(ToolRoot, "src", "RemoteToolGateway.MyPowerTools", "bin", configuration, "net10.0");
            if (File.Exists(Path.Combine(candidate, "RemoteToolGateway.MyPowerTools.dll"))) return candidate;
        }

        return null;
    }

    [Fact]
    public void PackageManifests_PointAtTheRealAssemblyAndTypes()
    {
        var module = JsonNode.Parse(File.ReadAllText(Path.Combine(ToolRoot, "package", "module.json")))!.AsObject();
        Assert.Equal("remote-tool-gateway", module["id"]!.GetValue<string>());
        Assert.Equal("inproc-dotnet", module["entrypoints"]!.AsArray()[0]!["kind"]!.GetValue<string>());
        Assert.Equal("RemoteToolGateway.MyPowerTools.dll", module["entrypoints"]!.AsArray()[0]!["assembly"]!.GetValue<string>());
        Assert.Equal("RemoteToolGateway.MyPowerTools.RemoteToolGatewayModule", module["entrypoints"]!.AsArray()[0]!["type"]!.GetValue<string>());
        Assert.Contains(module["requires"]!.AsArray(), require => require!["capability"]!.GetValue<string>() == "secret.store");
        Assert.DoesNotContain(module["entrypoints"]!.AsArray()[0]!["platforms"]!.AsArray(),
            platform => platform!.GetValue<string>().StartsWith("android", StringComparison.Ordinal));

        var tool = JsonNode.Parse(File.ReadAllText(Path.Combine(ToolRoot, "package", "ui", "tool.json")))!.AsObject();
        var route = tool["routes"]!.AsArray()[0]!.AsObject();
        Assert.Equal("surface/RemoteToolGateway.Surface.dll", route["surface"]!["assembly"]!.GetValue<string>());
        Assert.Equal("RemoteToolGateway.Surface.RemoteToolGatewaySurfaceFactory", route["surface"]!["type"]!.GetValue<string>());

        var surface = JsonNode.Parse(File.ReadAllText(Path.Combine(ToolRoot, "package", "ui", "control-page.json")))!.AsObject();
        Assert.Equal("detail-page", surface["kind"]!.GetValue<string>());
        Assert.Equal(route["surfaceId"]!.GetValue<string>(), surface["surfaceId"]!.GetValue<string>());

        var commands = JsonNode.Parse(File.ReadAllText(Path.Combine(ToolRoot, "package", "commands.index.json")))!.AsObject()["commands"]!.AsArray();
        foreach (var commandId in new[]
                 {
                     "remote-tool-gateway.inspect", "remote-tool-gateway.listener.start", "remote-tool-gateway.listener.stop",
                     "remote-tool-gateway.grant.create", "remote-tool-gateway.grant.update", "remote-tool-gateway.grant.revoke",
                     "remote-tool-gateway.grant.code", "remote-tool-gateway.confirmation.claim", "remote-tool-gateway.confirmation.resolve"
                 })
        {
            Assert.Contains(commands, command => command!["id"]!.GetValue<string>() == commandId);
        }
    }

    [Fact]
    public void PackageManifests_ValidateAgainstTheRepositorySchemas()
    {
        var root = RepositoryRoot();
        var pairs = new (string Document, string Schema)[]
        {
            (Path.Combine(ToolRoot, "package", "module.json"), Path.Combine(root, "schemas", "module.schema.json")),
            (Path.Combine(ToolRoot, "package", "ui", "tool.json"), Path.Combine(root, "schemas", "tool.schema.json")),
            (Path.Combine(ToolRoot, "package", "ui", "control-page.json"), Path.Combine(root, "schemas", "ui-surface.schema.json"))
        };
        foreach (var (document, schema) in pairs)
        {
            var jsonSchema = JsonSchema.FromText(File.ReadAllText(schema));
            using var parsed = System.Text.Json.JsonDocument.Parse(File.ReadAllText(document));
            var evaluation = jsonSchema.Evaluate(parsed.RootElement, new EvaluationOptions
            {
                OutputFormat = OutputFormat.List
            });
            var problems = evaluation.Details
                .Where(detail => !detail.IsValid)
                .Select(detail => $"{detail.InstanceLocation}: {detail.Errors?.ToString() ?? "invalid"}")
                .ToArray();
            Assert.True(evaluation.IsValid,
                $"{Path.GetFileName(document)} violates {Path.GetFileName(schema)}: {string.Join("; ", problems)}");
        }
    }

    [Fact]
    public void ModuleOutput_ResolvesItsRuntimeDependenciesFromTheModuleDirectory()
    {
        var directory = ModuleOutputDirectory();
        if (directory is null)
        {
            // The suite can run before the tool was ever built; point at the exact command instead
            // of pretending the packaging is verified.
            throw new Xunit.Sdk.XunitException("Build the module first: dotnet build tools/remote-tool-gateway/src/RemoteToolGateway.MyPowerTools/RemoteToolGateway.MyPowerTools.csproj");
        }

        var modulePath = Path.Combine(directory, "RemoteToolGateway.MyPowerTools.dll");
        var resolver = new AssemblyDependencyResolver(modulePath);
        foreach (var name in new[] { "RemoteToolGateway.Core", "MyPowerTools.HostControl.Client", "MyPowerTools.Protocol", "Grpc.Net.Client", "Google.Protobuf" })
        {
            var resolved = resolver.ResolveAssemblyToPath(new AssemblyName(name));
            Assert.False(string.IsNullOrEmpty(resolved), $"{name} was not resolvable from the module package directory.");
            Assert.True(File.Exists(resolved), $"{name} resolved to a missing file: {resolved}");
            Console.WriteLine($"[packaging] {name} -> {resolved}");
        }

        // The manifest's entry point type must exist in the built assembly.
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(modulePath);
        Assert.NotNull(assembly.GetType("RemoteToolGateway.MyPowerTools.RemoteToolGatewayModule", throwOnError: false));
    }

    [Fact]
    public void StagedPackage_WhenBuilt_ContainsTheModuleAndSurfaceAssemblies()
    {
        var package = Path.Combine(ToolRoot, "artifacts", "package");
        if (!Directory.Exists(package)) return;
        Assert.True(File.Exists(Path.Combine(package, "RemoteToolGateway.MyPowerTools.dll")));
        Assert.True(File.Exists(Path.Combine(package, "RemoteToolGateway.Core.dll")));
        Assert.True(File.Exists(Path.Combine(package, "MyPowerTools.HostControl.Client.dll")));
        Assert.True(File.Exists(Path.Combine(package, "ui", "surface", "RemoteToolGateway.Surface.dll")));
        Assert.True(File.Exists(Path.Combine(package, "module.json")));
        Assert.True(File.Exists(Path.Combine(package, "ui", "tool.json")));

        // The surface folder ships only the surface: AvaloniaSdk and Abstractions are resolved as
        // shared assemblies by every dotnet-surface host, so a second copy is package weight only.
        var surface = Path.Combine(package, "ui", "surface");
        Assert.False(File.Exists(Path.Combine(surface, "MyPowerTools.AvaloniaSdk.dll")));
        Assert.False(File.Exists(Path.Combine(surface, "MyPowerTools.Abstractions.dll")));
    }
}
