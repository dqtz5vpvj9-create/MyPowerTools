using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.HostControl;
using MyPowerTools.Runtime;
using MyPowerTools.Platform.Abstractions;

namespace ShortcutCenter.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("Control+shift+k", "Ctrl+Shift+K")]
    [InlineData("Command+Return", "Win+Enter")]
    [InlineData("Cmd+PgUp", "Win+PageUp")]
    [InlineData("alt+left", "Alt+Left")]
    [InlineData("ctrl+OemComma", "Ctrl+OemComma")]
    public void Gestures_have_one_identity(string input, string expected) => Assert.Equal(expected, ShortcutCatalog.Normalize(input));

    [Fact]
    public void Multiple_bindings_roundtrip_without_losing_legacy_arguments()
    {
        using var temp = new TemporaryDirectory();
        var store = new HotkeyStore(temp.Path);
        store.Set("sample", "run", "Ctrl+R", false, "{\"target\":\"original\"}");
        var definition = new ShortcutDefinition("sample.run", "sample.run", "Run", "Sample", "system", [new("Ctrl+R")], LegacyModuleId: "sample", LegacyHotkeyId: "run");
        store.UpdateShortcuts(1, [new("sample.run", [new("Ctrl+K"), new("Ctrl+Shift+K")])], [definition]);
        var reopened = new HotkeyStore(temp.Path);
        Assert.Equal(2, reopened.ReadShortcuts().Overrides.Single().Bindings.Count);
        Assert.Equal("{\"target\":\"original\"}", reopened.Get("sample", "run")!.CommandArgsJson);
        reopened.UpdateShortcuts(2, [new("sample.run", [], Reset: true)], [definition]);
        Assert.Empty(reopened.ReadShortcuts().Overrides);
        Assert.Equal("{\"target\":\"original\"}", reopened.Get("sample", "run")!.CommandArgsJson);
    }

    [Fact]
    public void Invalid_bulk_import_changes_nothing_and_conflict_does_not_overwrite()
    {
        using var temp = new TemporaryDirectory(); var store = new HotkeyStore(temp.Path);
        store.Set("sample", "run", "Ctrl+R", false, "{}");
        var original = File.ReadAllText(System.IO.Path.Combine(temp.Path, "hotkeys.json"));
        Assert.Throws<InvalidDataException>(() => store.UpdateShortcuts(1,
            [new("shell.refresh", [new("F6")]), new("bad", [new("Ctrl+NotAKey")])], []));
        Assert.Equal(original, File.ReadAllText(System.IO.Path.Combine(temp.Path, "hotkeys.json")));
        Assert.Throws<SettingsConflictException>(() => store.UpdateShortcuts(0, [new("shell.refresh", [new("F6")])], []));
        Assert.Equal(original, File.ReadAllText(System.IO.Path.Combine(temp.Path, "hotkeys.json")));
    }

    [Fact]
    public void Disabled_unbound_and_uninstalled_overrides_remain_distinct()
    {
        using var temp = new TemporaryDirectory(); var store = new HotkeyStore(temp.Path);
        var state = store.UpdateShortcuts(0, [new("disabled", [new("Ctrl+K")], true), new("unbound", []), new("uninstalled", [new("Cmd+R", "macos")])], []);
        Assert.Equal(3, new HotkeyStore(temp.Path).ReadShortcuts().Overrides.Count);
        Assert.True(state.Overrides.Single(item => item.Id == "disabled").Disabled);
        Assert.Empty(state.Overrides.Single(item => item.Id == "unbound").Bindings);
    }

    [Fact]
    public void Existing_pascal_case_hotkeys_json_is_read_without_migration_writes()
    {
        using var temp = new TemporaryDirectory();
        const string json = """{"Revision":9,"Overrides":[{"ModuleId":"sample","HotkeyId":"run","Gesture":"Ctrl+R","Disabled":false,"CommandArgsJson":"{}"}],"UpdatedAt":"2026-01-01T00:00:00Z"}""";
        var path = System.IO.Path.Combine(temp.Path, "hotkeys.json"); File.WriteAllText(path, json);
        var state = new HotkeyStore(temp.Path).ReadShortcuts();
        Assert.Equal(9ul, state.Revision); Assert.Equal("sample.run", state.Overrides.Single().Id);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void Settings_transport_roundtrips_catalog_without_numeric_or_casing_drift()
    {
        var snapshot = TestCatalog.Create([new("shell.refresh", [new("F7")])]);
        var json = (JsonObject)JsonSerializer.SerializeToNode(snapshot, ShortcutCatalog.JsonOptions)!;
        var result = JsonStructMapper.ToJsonObject(JsonStructMapper.ToStruct(json)).Deserialize<ShortcutCatalogSnapshot>(ShortcutCatalog.JsonOptions)!;
        Assert.Equal(snapshot.Configuration.Revision, result.Configuration.Revision);
        Assert.Equal("F7", result.Configuration.Overrides.Single().Bindings.Single().Gesture);
        Assert.Equal(snapshot.Commands.Count, result.Commands.Count);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mpt-shortcut-tests-" + Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}

internal static class TestCatalog
{
    public static ShortcutCatalogSnapshot Create(IReadOnlyList<ShortcutOverride>? overrides = null,
        IReadOnlyList<ShortcutDefinition>? extra = null, string platform = "windows") =>
        new(new(0, overrides ?? []), ShortcutCatalog.ShellCommands.Concat(extra ?? []).ToArray(), [], platform);
}
