using System.Text.Json;
using NssmManager.Runtime;

namespace NssmManager.Tests;

public sealed class ToolManifestTests
{
    private static readonly JsonElement[] Commands = ReadCommands();

    [Fact]
    public void no_command_is_declared_as_a_broker_request()
    {
        // Mutating NSSM commands must reach the runtime transport. A broker.request
        // execution type would skip that path.
        Assert.NotEmpty(Commands);
        Assert.All(Commands, command => Assert.NotEqual("broker.request", ExecutionType(command)));
    }

    [Fact]
    public void mutating_commands_execute_through_the_module_transport_under_broker_approval()
    {
        var mutating = Commands
            .Where(command => command.TryGetProperty("execution", out var execution)
                && execution.TryGetProperty("mutatesSystemState", out var mutates)
                && mutates.GetBoolean())
            .ToArray();
        Assert.NotEmpty(mutating);
        Assert.All(mutating, command =>
        {
            var execution = command.GetProperty("execution");
            Assert.Equal("module.execute", execution.GetProperty("type").GetString());
            Assert.True(execution.GetProperty("brokerApprovalOnly").GetBoolean());
            Assert.True(execution.GetProperty("mutatesSystemState").GetBoolean());
            // Elevation is forced at runtime (NssmElevatedClient.PrivilegedOperations and the
            // Broker whitelist), so the manifest must report it instead of contradicting the
            // declared elevated permission. It only drives the Shell danger/elevation marker.
            Assert.True(command.GetProperty("requiresElevation").GetBoolean());
        });
    }

    [Fact]
    public void manifest_elevation_flags_match_the_runtime_privileged_action_whitelist()
    {
        Assert.All(Commands, command =>
        {
            var id = command.GetProperty("id").GetString()!;
            var declared = command.TryGetProperty("requiresElevation", out var value) && value.GetBoolean();
            Assert.Equal(NssmElevatedClient.RequiresElevation(id), declared);
        });
    }

    private static string? ExecutionType(JsonElement command) =>
        command.TryGetProperty("execution", out var execution) && execution.TryGetProperty("type", out var type)
            ? type.GetString()
            : null;

    private static JsonElement[] ReadCommands()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(ManifestPath()));
        return manifest.RootElement.GetProperty("commands").EnumerateArray().Select(command => command.Clone()).ToArray();
    }

    private static string ManifestPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tool.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the nssm-manager tool manifest.");
    }
}
