using System.Text.Json;
using System.Text.Json.Nodes;
using ImeManager.MyPowerTools;
using ImeManager.Tool;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.Tests;

public sealed class ImeManagerWorkflowTests
{
    private const string Us = "0409:00000409";
    private const string Zh = "0804:00000804";
    private const string Jp = "0411:00000411";

    [Fact]
    public async Task Discard_restores_enabled_order_and_persisted_management_order()
    {
        using var harness = new Harness();
        await harness.Model.InitializeAsync();
        harness.Model.SelectedItem = harness.Model.Items[1];
        await harness.Model.MoveUpCommand.ExecuteAsync();
        Assert.Equal([Zh, Us], harness.Model.Items.Select(row => row.TipString));
        Assert.True(harness.Model.IsDirty);
        await harness.Model.DiscardCommand.ExecuteAsync();
        Assert.Equal([Us, Zh], harness.Model.Items.Select(row => row.TipString));
        Assert.False(harness.Model.IsDirty);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(harness.Directory, "managed-input-methods.json")));
        Assert.Equal([Us, Zh], saved!["tipStrings"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(0, harness.ApplyCalls);
    }

    [Fact]
    public async Task Add_filter_default_hotkeys_apply_and_refresh_preserve_disabled_inputs()
    {
        using var harness = new Harness();
        var model = harness.Model;
        await model.InitializeAsync();
        Assert.False(model.CanApply);
        await model.AddCommand.ExecuteAsync();
        Assert.True(model.IsAddPanelOpen);
        model.FilterText = "missing";
        Assert.Empty(model.AvailableItems);
        model.FilterText = "jApAnEsE";
        model.SelectedAvailable = Assert.Single(model.AvailableItems);
        await model.AddCommand.ExecuteAsync();
        Assert.False(model.IsAddPanelOpen);
        Assert.Equal(3, model.Items.Count);
        Assert.Empty(model.AvailableItems);
        model.SelectedItem = model.Items.Single(row => row.TipString == Jp);
        await model.SetDefaultCommand.ExecuteAsync();
        model.Items.Single(row => row.TipString == Zh).IsChecked = false;
        model.LanguageHotkey = ImeManagerViewModel.HotkeyChoices.Single(choice => choice.Value == SwitchHotkey.NotAssigned);
        model.WinSpaceMapsToShift = true;
        await model.ApplyCommand.ExecuteAsync();
        Assert.Equal(1, harness.ApplyCalls);
        Assert.Equal([Us, Jp], harness.Snapshot.Enabled.Select(row => row.TipString));
        Assert.Equal(Jp, harness.Snapshot.DefaultTipString);
        Assert.Equal(SwitchHotkey.NotAssigned, harness.Snapshot.Hotkeys.LanguageHotkey);
        Assert.True(harness.Snapshot.WinSpaceMapsToShift);
        Assert.False(model.IsDirty);
        Assert.False(model.IsBusy);
        Assert.False(model.Items.Single(row => row.TipString == Zh).IsChecked);
        await model.RefreshCommand.ExecuteAsync();
        Assert.Equal(3, model.Items.Count);
        Assert.False(model.Items.Single(row => row.TipString == Zh).IsChecked);
    }

    [Fact]
    public async Task Empty_selection_is_rejected_and_discard_restores_hotkeys_and_remapping()
    {
        using var harness = new Harness();
        var model = harness.Model;
        await model.InitializeAsync();
        await model.InvertSelectionCommand.ExecuteAsync();
        Assert.All(model.Items, row => Assert.False(row.IsChecked));
        await model.ApplyCommand.ExecuteAsync();
        Assert.Contains("至少保留", model.ActionMessage);
        Assert.Equal(0, harness.ApplyCalls);
        model.WinSpaceMapsToShift = true;
        model.LayoutHotkey = ImeManagerViewModel.HotkeyChoices[3];
        await model.DiscardCommand.ExecuteAsync();
        Assert.All(model.Items, row => Assert.True(row.IsChecked));
        Assert.Equal(SwitchHotkey.CtrlShift, model.LayoutHotkey.Value);
        Assert.False(model.WinSpaceMapsToShift);
        Assert.False(model.IsDirty);
        await model.SelectAllCommand.ExecuteAsync();
        Assert.False(model.IsDirty);
    }

    [Fact]
    public async Task Runtime_failure_preserves_draft_and_refresh_releases_busy_state()
    {
        using var harness = new Harness();
        var model = harness.Model;
        await model.InitializeAsync();
        model.Items[1].IsChecked = false;
        harness.FailApply = true;
        await model.ApplyCommand.ExecuteAsync();
        Assert.Contains("应用失败", model.StatusText);
        Assert.True(model.IsDirty);
        Assert.False(model.IsBusy);
        Assert.Equal(2, harness.Snapshot.Enabled.Count);
        harness.FailSnapshot = true;
        await model.RefreshCommand.ExecuteAsync();
        Assert.Contains("读取失败", model.StatusText);
        Assert.False(model.IsBusy);
        Assert.True(model.IsDirty);
        harness.FailSnapshot = false;
        await model.RefreshCommand.ExecuteAsync();
        Assert.False(model.IsDirty);
        Assert.All(model.Items, row => Assert.True(row.IsChecked));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"tipStrings\":[null,5,\"bad\",\"0x0409:0x00000409\",\"0409:00000409\"]}")]
    public async Task Invalid_or_duplicate_managed_storage_recovers_without_touching_system(string storage)
    {
        using var harness = new Harness();
        await File.WriteAllTextAsync(Path.Combine(harness.Directory, "managed-input-methods.json"), storage);
        await harness.Model.InitializeAsync();
        Assert.Equal([Us, Zh], harness.Model.Items.Select(row => row.TipString));
        Assert.False(harness.Model.IsBusy);
        Assert.Equal(0, harness.ApplyCalls);
    }

    private sealed class Harness : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "mpt-ime-e2e-" + Guid.NewGuid().ToString("N"));
        public InputMethodSnapshot Snapshot { get; private set; } = new("windows",
            [Info(Us, "English", true, true), Info(Zh, "Chinese", true, false)],
            [Info(Jp, "Japanese", false, false)], Us, SwitchHotkeys.WindowsDefault);
        public ImeManagerViewModel Model { get; }
        public int ApplyCalls { get; private set; }
        public bool FailApply { get; set; }
        public bool FailSnapshot { get; set; }

        public Harness()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Model = new ImeManagerViewModel(new MptAvaloniaSurfaceContext("ime-manager", "main", Directory, "light",
                ExecuteAsync, (_, _, _) => Task.CompletedTask, null!, _ => { }));
        }

        private Task<CommandExecutionResult> ExecuteAsync(string command, JsonObject? arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if ((command == "ime-manager.apply" && FailApply) || (command == "ime-manager.snapshot" && FailSnapshot))
                throw new IOException("controlled runtime failure");
            JsonObject payload;
            if (command == "ime-manager.apply")
            {
                ApplyCalls++;
                var original = InputMethodPlanner.FromSnapshot(Snapshot);
                var desired = new InputMethodPlan(arguments!["enabledTipStrings"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray(),
                    arguments["defaultTipString"]!.GetValue<string>(),
                    new(arguments["languageHotkey"]!.Deserialize<SwitchHotkey>(ImeManagerJson.Compact),
                        arguments["layoutHotkey"]!.Deserialize<SwitchHotkey>(ImeManagerJson.Compact)));
                InputMethodPlanner.Validate(desired, InputMethodPlanner.CatalogSet(Snapshot));
                var infos = Snapshot.Enabled.Concat(Snapshot.Available).ToDictionary(info => info.TipString);
                Snapshot = Snapshot with
                {
                    Enabled = desired.EnabledTipStrings.Select(tip => infos[tip] with { IsEnabled = true, IsDefault = tip == desired.DefaultTipString }).ToArray(),
                    Available = infos.Values.Where(info => !desired.EnabledTipStrings.Contains(info.TipString)).Select(info => info with { IsEnabled = false, IsDefault = false }).ToArray(),
                    DefaultTipString = desired.DefaultTipString, Hotkeys = desired.Hotkeys,
                    WinSpaceMapsToShift = arguments["winSpaceMapsToShift"]!.GetValue<bool>()
                };
                payload = JsonSerializer.SerializeToNode(new InputMethodApplyResult(Snapshot, InputMethodPlanner.Diff(original, desired)), ImeManagerJson.Compact)!.AsObject();
            }
            else
            {
                Assert.Equal("ime-manager.snapshot", command);
                payload = new JsonObject { ["snapshot"] = JsonSerializer.SerializeToNode(Snapshot, ImeManagerJson.Compact) };
            }
            return Task.FromResult(new CommandExecutionResult("ime-manager", command, "completed", true,
                new JsonObject { ["result"] = new JsonObject { ["state"] = "ready", ["payload"] = payload } }.ToJsonString()));
        }

        private static InputMethodInfo Info(string tip, string language, bool enabled, bool isDefault)
        {
            Assert.True(ParsedTipString.TryParse(tip, out var parsed));
            return new(tip, parsed.LanguageId, language, language + " keyboard", InputMethodKind.KeyboardLayout,
                enabled, isDefault, Guid.Empty, Guid.Empty, parsed.KeyboardLayoutId);
        }

        public void Dispose()
        {
            Model.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
