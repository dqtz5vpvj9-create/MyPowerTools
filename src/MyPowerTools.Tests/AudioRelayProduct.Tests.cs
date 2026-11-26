using System.Text.Json.Nodes;
using AudioRelay.MyPowerTools;
using AudioRelay.Tool;

namespace MyPowerTools.Tests;

public sealed class AudioRelayProductTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string ToolRoot = Path.Combine(Root, "tools", "audio-relay", "sdk-tool");

    [Fact]
    public void Locator_prefers_registered_executable_and_preserves_registered_version()
    {
        var registered = @"C:\Program Files\AudioRelay\AudioRelay.exe";
        var installation = AudioRelayInstallationLocator.Find(
            [new AudioRelayProductRegistration(@"C:\Program Files\AudioRelay", $"\"{registered}\",0", "0.27.5")],
            [@"C:\fallback\AudioRelay.exe"],
            path => string.Equals(path, registered, StringComparison.OrdinalIgnoreCase),
            _ => null);

        Assert.NotNull(installation);
        Assert.Equal(registered, installation.ExecutablePath);
        Assert.Equal("0.27.5", installation.Version);
    }

    [Theory]
    [InlineData("\"C:\\Apps\\AudioRelay.exe\",0", "C:\\Apps\\AudioRelay.exe")]
    [InlineData("C:\\Apps\\AudioRelay.exe", "C:\\Apps\\AudioRelay.exe")]
    public void Display_icon_parser_removes_quotes_and_icon_index(string value, string expected)
    {
        Assert.Equal(expected, AudioRelayInstallationLocator.ParseDisplayIcon(value));
    }

    [Fact]
    public void Manifest_exposes_status_launch_and_official_guides_through_isolated_runtime()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(ToolRoot, "tool.json")))!.AsObject();
        Assert.Equal("audio-relay", manifest["toolId"]!.GetValue<string>());
        Assert.Equal("dotnet-surface", manifest["type"]!.GetValue<string>());
        Assert.Equal("stdio-jsonrpc", manifest["runtime"]!["transport"]!.GetValue<string>());
        Assert.EndsWith("AudioRelay.Runtime.exe", manifest["runtime"]!["command"]!.GetValue<string>(), StringComparison.Ordinal);

        var commands = manifest["commands"]!.AsArray()
            .Select(node => node!["id"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("audio-relay.snapshot", commands);
        Assert.Contains("audio-relay.launch", commands);
        Assert.Contains("audio-relay.open-downloads", commands);
        Assert.Contains("audio-relay.open-send-audio-guide", commands);
        Assert.Contains("audio-relay.open-microphone-guide", commands);

        var route = manifest["routes"]!.AsArray().Single()!.AsObject();
        Assert.Equal(typeof(AudioRelaySurfaceFactory).FullName, route["surface"]!["type"]!.GetValue<string>());
        Assert.Contains("audiorelay.net", File.ReadAllText(Path.Combine(ToolRoot, "src", "AudioRelay.Runtime", "Program.cs")), StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MyPowerTools.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("MyPowerTools repository root was not found.");
    }
}
