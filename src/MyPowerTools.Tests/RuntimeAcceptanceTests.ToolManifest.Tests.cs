using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using MyPowerTools.Packaging;

namespace MyPowerTools.Tests;

public sealed partial class RuntimeAcceptanceTests
{
    private static readonly Lazy<JsonSchema> ToolContractManifestSchema = new(
        () => MptJsonSchemas.FromFile(Path.Combine(Root, "schemas", "tool.schema.json")));

    [Fact]
    public void Module_manifest_tools_are_optional_and_map_as_relative_paths()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-tool-manifest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        var manifestPath = Path.Combine(packageRoot, "module.json");

        var manifest = CreateMinimalModuleManifest();
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        var reader = new PackageReader();
        var legacyModule = reader.ReadModuleDefinition(manifestPath).Manifest;

        Assert.Empty(legacyModule.Tools);

        manifest["tools"] = new JsonArray("tools/remote-notifications.tool.json");
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        var module = reader.ReadModuleDefinition(manifestPath).Manifest;
        var report = new SchemaPackageValidator(Path.Combine(Root, "schemas")).ValidatePackageDirectory(packageRoot);

        Assert.Equal("tools/remote-notifications.tool.json", Assert.Single(module.Tools));
        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public void Tool_manifest_schema_and_packaging_model_share_the_product_contract()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-tool-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        var toolPath = Path.Combine(packageRoot, "remote-notifications.tool.json");
        var tool = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["toolId"] = "remote-notifications",
            ["ownerModuleId"] = "android-tools.notifications",
            ["title"] = "Remote Notifications",
            ["description"] = "Read and manage notifications from a remote Android device.",
            ["icon"] = "notifications",
            ["category"] = "Connectivity",
            ["availability"] = "paused",
            ["primaryRouteId"] = "inbox",
            ["routes"] = new JsonArray
            {
                new JsonObject
                {
                    ["routeId"] = "inbox",
                    ["surfaceId"] = "android-tools.notifications.inbox",
                    ["title"] = "Inbox",
                    ["icon"] = "inbox"
                },
                new JsonObject
                {
                    ["routeId"] = "diagnostics",
                    ["surfaceId"] = "android-tools.notifications.diagnostics",
                    ["title"] = "Troubleshooting",
                    ["icon"] = "wrench"
                }
            },
            ["homeCard"] = new JsonObject
            {
                ["summary"] = "Review unread notifications.",
                ["primaryActionLabel"] = "Open inbox",
                ["statusBinding"] = "connection.state",
                ["order"] = 10
            }
        };
        File.WriteAllText(toolPath, tool.ToJsonString());

        var schema = ToolContractManifestSchema.Value;
        using var document = JsonDocument.Parse(File.ReadAllText(toolPath));
        var evaluation = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        var mapped = new PackageReader().ReadJson<MptToolManifest>(toolPath);

        Assert.True(evaluation.IsValid);
        Assert.Equal("remote-notifications", mapped.ToolId);
        Assert.Equal("android-tools.notifications", mapped.OwnerModuleId);
        Assert.Equal("inbox", mapped.PrimaryRouteId);
        Assert.Equal("paused", mapped.Availability);
        Assert.Equal(2, mapped.Routes.Count);
        Assert.Equal("android-tools.notifications.inbox", mapped.Routes[0].SurfaceId);
        Assert.Equal("Open inbox", mapped.HomeCard.PrimaryActionLabel);
        Assert.Equal("connection.state", mapped.HomeCard.StatusBinding);
        Assert.Equal(10, mapped.HomeCard.Order);
    }

    [Fact]
    public void Tool_manifest_schema_requires_an_explicit_primary_route()
    {
        var tool = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["toolId"] = "remote-notifications",
            ["ownerModuleId"] = "android-tools.notifications",
            ["title"] = "Remote Notifications",
            ["description"] = "Read notifications.",
            ["icon"] = "notifications",
            ["category"] = "Connectivity",
            ["routes"] = new JsonArray
            {
                new JsonObject
                {
                    ["routeId"] = "inbox",
                    ["surfaceId"] = "android-tools.notifications.inbox"
                }
            },
            ["homeCard"] = new JsonObject { ["summary"] = "Review unread notifications." }
        };
        var schema = ToolContractManifestSchema.Value;
        using var document = JsonDocument.Parse(tool.ToJsonString());

        var evaluation = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }

    private static JsonObject CreateMinimalModuleManifest()
    {
        return new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "android-tools.notifications",
            ["packageId"] = "android-tools-suite",
            ["displayName"] = "Remote Notifications Module",
            ["version"] = "1.0.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "grpc-ipc",
                    ["priority"] = 100,
                    ["command"] = "powertoold.exe"
                }
            },
            ["capabilities"] = new JsonArray("status", "commands")
        };
    }
}
