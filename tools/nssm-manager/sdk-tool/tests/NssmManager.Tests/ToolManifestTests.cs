using System.Text.Json;

namespace NssmManager.Tests;

public sealed class ToolManifestTests
{
    private static readonly JsonElement[] Commands = ReadCommands();

    [Fact]
    public void no_command_is_declared_as_a_broker_request()
    {
        // MptHostRuntime.BrokerRequestCommand answers "broker.request" with a permission-required
        // stub and never reaches the runtime, so mutating commands must not use it.
        Assert.NotEmpty(Commands);
        Assert.All(Commands, command => Assert.NotEqual("broker.request", ExecutionType(command)));
    }

    [Fact]
    public void elevated_commands_execute_through_the_module_transport_under_broker_approval()
    {
        var elevated = Commands
            .Where(command => command.TryGetProperty("requiresElevation", out var value) && value.GetBoolean())
            .ToArray();
        Assert.NotEmpty(elevated);
        Assert.All(elevated, command =>
        {
            var execution = command.GetProperty("execution");
            Assert.Equal("module.execute", execution.GetProperty("type").GetString());
            Assert.True(execution.GetProperty("brokerApprovalOnly").GetBoolean());
            Assert.True(execution.GetProperty("mutatesSystemState").GetBoolean());
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
