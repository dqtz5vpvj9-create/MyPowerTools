using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.UI.Controls;
using MyPowerTools.WebSurface.Avalonia;
using MyPowerTools.WebToolHost;

namespace ShortcutCenter.Tests;

public sealed class WebShortcutTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_host_frames_preserve_the_gesture_and_input_context(bool input)
    {
        var envelope = JsonSerializer.Serialize(new { gesture = "Ctrl+Shift+P", textInput = input });
        Assert.True(envelope.Length > 32); // The old host reader rejected every current envelope.
        using var output = new StringWriter();
        WebToolHostProtocol.WriteShortcut(envelope, output);
        Assert.True(WebSurfaceControl.TryReadHostEvent(output.ToString().TrimEnd(), Environment.ProcessId, out var frame));
        Assert.Equal(WebSurfaceControl.HostProcessEventKind.Shortcut, frame.Kind);
        Assert.Equal(envelope, frame.Value);
        Assert.True(WebShortcutMessage.TryRead(frame.Value, out var gesture, out var textInput));
        Assert.Equal("Ctrl+Shift+P", gesture);
        Assert.Equal(input, textInput);
    }

    [Fact]
    public void Legacy_host_gestures_remain_compatible_without_losing_text_input_ownership()
    {
        using var output = new StringWriter();
        WebToolHostProtocol.WriteShortcut("Ctrl+Shift+P", output);
        Assert.True(WebSurfaceControl.TryReadHostEvent(output.ToString().TrimEnd(), Environment.ProcessId, out var frame));
        Assert.True(WebShortcutMessage.TryRead(frame.Value, out var gesture, out var textInput));
        Assert.Equal("Ctrl+Shift+P", gesture);
        Assert.True(textInput);
    }

    [Theory]
    [InlineData("{\"gesture\":\"Ctrl+K\"}")]
    [InlineData("{\"gesture\":\"Ctrl+K\",\"textInput\":\"false\"}")]
    [InlineData("{\"gesture\":null,\"textInput\":false}")]
    [InlineData("{\"gesture\":\"\",\"textInput\":false}")]
    public void Malformed_envelopes_cannot_be_mistaken_for_non_text_context(string envelope)
    {
        using var output = new StringWriter();
        WebToolHostProtocol.WriteShortcut(envelope, output);
        Assert.False(WebSurfaceControl.TryReadHostEvent(output.ToString().TrimEnd(), Environment.ProcessId, out _));
        Assert.False(WebShortcutMessage.TryRead(envelope, out _, out var textInput));
        Assert.True(textInput);
    }

    [AvaloniaFact]
    public async Task Chrome_hint_tracks_actual_configuration_rebinding_and_disable()
    {
        var chrome = new ShellChromeViewModel(ShellWorkspaceController.PageLabels);
        await using var controller = new ShellWorkspaceController(chrome, new MptSearchBox(),
            new ContentControl(), new ContentControl(), new ContentControl(), new ContentControl());
        var client = (ShortcutConfigurationClient)typeof(ShellWorkspaceController)
            .GetField("_shortcuts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
        var accept = typeof(ShortcutConfigurationClient)
            .GetMethod("Accept", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var snapshot = new ShortcutCatalogSnapshot(new(0, []), ShortcutCatalog.ShellCommands, [], "macos");
        accept.Invoke(client, [snapshot]);
        Assert.Equal(client.Hint("shell.command-palette.open"), chrome.CommandPaletteShortcutHint);
        Assert.True(chrome.HasCommandPaletteShortcutHint);
        snapshot = snapshot with { Configuration = new(1, [new("shell.command-palette.open", [new("Win+F9", "macos")])]) };
        accept.Invoke(client, [snapshot]);
        Assert.Equal("Cmd+F9", chrome.CommandPaletteShortcutHint);
        snapshot = snapshot with { Configuration = new(2, [new("shell.command-palette.open", [new("Win+F9", "macos")], true)]) };
        accept.Invoke(client, [snapshot]);
        Assert.Empty(chrome.CommandPaletteShortcutHint);
        Assert.False(chrome.HasCommandPaletteShortcutHint);
    }
}
