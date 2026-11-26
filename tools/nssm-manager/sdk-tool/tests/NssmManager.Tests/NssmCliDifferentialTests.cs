using NssmManager.Windows;
using NssmManager.Runtime;
using System.Text.Json.Nodes;

namespace NssmManager.Tests;

public sealed class NssmCliDifferentialTests
{
    [Fact]
    public void usage_matches_upstream()
    {
        using var output = new StringWriter();
        Assert.Equal(7, NssmCore.usage(7, output));
        Assert.Contains("NSSM: The non-sucking service manager", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Version 2.24-101-g897c7ad 64-bit, 2017-04-26", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void elevated_mutations_preserve_exit_codes()
    {
        var arguments = new[] { "install", "service" };
        Assert.Equal(111, NssmCore.elevate(arguments, received =>
        {
            Assert.Same(arguments, received);
            return 111;
        }));
    }

    [Theory]
    [InlineData("nssm-manager.install", true)]
    [InlineData("nssm-manager.apply", true)]
    [InlineData("nssm-manager.remove", true)]
    [InlineData("nssm-manager.control", true)]
    [InlineData("nssm-manager.migrate", true)]
    [InlineData("nssm-manager.registry-set", true)]
    [InlineData("nssm-manager.registry-reset", true)]
    [InlineData("nssm-manager.imagepath", true)]
    [InlineData("nssm-manager.rollback", true)]
    [InlineData("nssm-manager.health", false)]
    [InlineData("nssm-manager.list", false)]
    [InlineData("nssm-manager.get", false)]
    [InlineData("nssm-manager.validate", false)]
    public void every_mutating_runtime_operation_requires_elevation(string operation, bool expected)
    {
        Assert.Equal(expected, NssmElevatedClient.RequiresElevation(operation));
    }

    [Fact]
    public void mutating_surface_commands_stay_module_execute_with_broker_approval()
    {
        var sdkToolRoot = FindSdkToolRoot();
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(sdkToolRoot, "tool.json")))!.AsObject();
        var commands = manifest["commands"]!.AsArray()
            .Select(command => command!.AsObject())
            .Where(command => command["id"]!.GetValue<string>() is
                "nssm-manager.install" or
                "nssm-manager.apply" or
                "nssm-manager.remove" or
                "nssm-manager.control" or
                "nssm-manager.migrate" or
                "nssm-manager.rollback")
            .ToArray();

        Assert.Equal(6, commands.Length);
        Assert.All(commands, command =>
        {
            // The runtime always forces UAC for these actions, so the manifest must say so;
            // requiresElevation drives the Shell danger/elevation marker only. The execution
            // route stays module.execute + brokerApprovalOnly and never the Shell permission gate.
            Assert.True(command["requiresElevation"]!.GetValue<bool>());
            Assert.Equal("module.execute", command["execution"]!["type"]!.GetValue<string>());
            Assert.True(command["execution"]!["brokerApprovalOnly"]!.GetValue<bool>());
        });
    }

    [Fact]
    public void tool_surfaces_do_not_declare_a_shell_permission_prompt()
    {
        var sdkToolRoot = FindSdkToolRoot();
        foreach (var file in new[] { "dashboard-card.json", "detail-page.json", "settings.json" })
        {
            var surface = File.ReadAllText(Path.Combine(sdkToolRoot, "ui", file));
            Assert.DoesNotContain("permission-required", surface, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MptPermissionPrompt", surface, StringComparison.Ordinal);
        }
    }

    private static string FindSdkToolRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var manifest = Path.Combine(current.FullName, "tool.json");
            if (File.Exists(manifest))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the nssm-manager sdk-tool root.");
    }
}
