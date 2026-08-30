using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using System.Xml.Linq;
using ImeManager.MyPowerTools;
using ImeManager.Tool;

namespace MyPowerTools.Tests;

public sealed class ImeManagerProductTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string ToolRoot = Path.Combine(Root, "tools", "ime-manager", "sdk-tool");

    [Fact]
    public void Tip_strings_canonicalize_keyboard_and_text_service_forms()
    {
        Assert.True(ParsedTipString.TryParse("0x0409:0x00000409", out var keyboard));
        Assert.Equal("0409:00000409", keyboard.Canonical);
        Assert.Equal(InputMethodKind.KeyboardLayout, keyboard.Kind);
        Assert.Equal((ushort)0x0409, keyboard.LanguageId);
        Assert.Equal(0x00000409u, keyboard.KeyboardLayoutId);

        Assert.True(ParsedTipString.TryParse(
            "0804:{A028AE76-01B1-46C2-99C4-ACD9858AE02F}{B5FE1F02-D5F2-4445-9C03-C568F23C99A1}",
            out var tip));
        Assert.Equal(InputMethodKind.TextService, tip.Kind);
        Assert.Equal(
            "0804:{A028AE76-01B1-46C2-99C4-ACD9858AE02F}{B5FE1F02-D5F2-4445-9C03-C568F23C99A1}",
            tip.Canonical);
        Assert.False(ParsedTipString.TryParse("not-a-tip", out _));
        Assert.False(ParsedTipString.TryParse("", out _));
    }

    [Fact]
    public void Assembly_item_values_round_trip_for_keyboard_and_text_service()
    {
        Assert.True(ParsedTipString.TryParse("0409:00000409", out var keyboard));
        Assert.True(ParsedTipString.TryParseAssemblyItem(
            0x0409,
            keyboard.ToAssemblyItemValue(),
            out var restoredKeyboard));
        Assert.Equal(keyboard, restoredKeyboard);

        Assert.True(ParsedTipString.TryParse(
            "0804:{A028AE76-01B1-46C2-99C4-ACD9858AE02F}{B5FE1F02-D5F2-4445-9C03-C568F23C99A1}",
            out var tip));
        Assert.True(ParsedTipString.TryParseAssemblyItem(
            0x0804,
            tip.ToAssemblyItemValue(),
            out var restoredTip));
        Assert.Equal(tip, restoredTip);
    }

    [Fact]
    public void Planner_adds_reorders_and_rejects_an_empty_switch_list()
    {
        var snapshot = SampleSnapshot();
        var catalog = InputMethodPlanner.CatalogSet(snapshot);
        var plan = InputMethodPlanner.FromSnapshot(snapshot);

        Assert.Equal(["0804:00000804", "0409:00000409"], plan.EnabledTipStrings);
        Assert.Equal("0804:00000804", plan.DefaultTipString);

        plan = InputMethodPlanner.Add(plan, "0804:{A028AE76-01B1-46C2-99C4-ACD9858AE02F}{B5FE1F02-D5F2-4445-9C03-C568F23C99A1}", catalog);
        Assert.Equal(3, plan.EnabledTipStrings.Count);

        plan = InputMethodPlanner.Move(plan, "0409:00000409", -1, catalog);
        Assert.Equal("0409:00000409", plan.EnabledTipStrings[0]);

        plan = InputMethodPlanner.SetDefault(plan, "0409:00000409", catalog);
        Assert.Equal("0409:00000409", plan.DefaultTipString);

        plan = InputMethodPlanner.Remove(plan, "0804:00000804", catalog);
        plan = InputMethodPlanner.Remove(plan, "0804:{A028AE76-01B1-46C2-99C4-ACD9858AE02F}{B5FE1F02-D5F2-4445-9C03-C568F23C99A1}", catalog);
        Assert.Equal(["0409:00000409"], plan.EnabledTipStrings);

        var empty = Assert.Throws<InvalidOperationException>(
            () => InputMethodPlanner.Remove(plan, "0409:00000409", catalog));
        Assert.Contains("至少保留", empty.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_apply_enables_disables_and_restores_order_through_the_platform()
    {
        var snapshot = SampleSnapshot();
        var platform = new FakeInputMethodPlatform(snapshot);
        var catalog = new InputMethodCatalog(platform);
        var plan = InputMethodPlanner.FromSnapshot(snapshot);
        var known = InputMethodPlanner.CatalogSet(snapshot);
        plan = InputMethodPlanner.Add(
            plan,
            "0804:{A028AE76-01B1-46C2-99C4-ACD9858AE02F}{B5FE1F02-D5F2-4445-9C03-C568F23C99A1}",
            known);
        plan = InputMethodPlanner.Remove(plan, "0409:00000409", known);
        plan = InputMethodPlanner.SetDefault(plan, "0804:00000804", known);
        plan = InputMethodPlanner.SetHotkeys(
            plan,
            new SwitchHotkeys(SwitchHotkey.CtrlShift, SwitchHotkey.NotAssigned),
            known);

        var result = catalog.Apply(plan);

        Assert.Equal(["0804:{A028AE76-01B1-46C2-99C4-ACD9858AE02F}{B5FE1F02-D5F2-4445-9C03-C568F23C99A1}"], platform.Enabled);
        Assert.Equal(["0409:00000409"], platform.Disabled);
        Assert.Equal(
            ["0804:00000804", "0804:{A028AE76-01B1-46C2-99C4-ACD9858AE02F}{B5FE1F02-D5F2-4445-9C03-C568F23C99A1}"],
            platform.Order);
        Assert.Equal("0804:00000804", platform.DefaultTip);
        Assert.Equal(SwitchHotkey.CtrlShift, platform.Hotkeys.LanguageHotkey);
        Assert.True(platform.Notified);
        Assert.True(result.Diff.HasChanges);
        Assert.Equal(2, result.Snapshot.Enabled.Count);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Enabled_order_ignores_preload_language_placeholders_already_covered_by_user_profile()
    {
        var merged = WindowsInputMethodPlatform.MergeEnabledOrder(
            [
                "0804:{86598FB9-66A2-463E-B9C2-AEB906D477AD}{607FDF85-FCC8-4DBD-A365-41296F980C9C}"
            ],
            [
                "0804:00000804",
                "0409:00000409"
            ]);

        Assert.Equal(
            [
                "0804:{86598FB9-66A2-463E-B9C2-AEB906D477AD}{607FDF85-FCC8-4DBD-A365-41296F980C9C}",
                "0409:00000409"
            ],
            merged);
    }

    [Fact]
    public void Managed_scope_keeps_disabled_inputs_visible_after_apply()
    {
        string[] original =
        [
            "0804:{86598FB9-66A2-463E-B9C2-AEB906D477AD}{607FDF85-FCC8-4DBD-A365-41296F980C9C}",
            "0804:00000804",
            "0409:00000409"
        ];
        var catalog = original.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var managed = ImeManagerViewModel.MergeManagedTipStrings(
            original,
            [],
            [original[0]],
            catalog);

        Assert.Equal(original, managed);
    }

    [Fact]
    public void Standalone_sdk_manifest_and_project_use_the_supported_dotnet_surface_contract()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(ToolRoot, "tool.json")))!.AsObject();
        var primaryRouteId = manifest["primaryRouteId"]!.GetValue<string>();
        var route = manifest["routes"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(item => item["routeId"]!.GetValue<string>() == primaryRouteId);
        var surface = route["surface"]!.AsObject();

        Assert.Equal("ime-manager", manifest["toolId"]!.GetValue<string>());
        Assert.Equal("dotnet-surface", manifest["type"]!.GetValue<string>());
        Assert.Equal("dotnet", surface["kind"]!.GetValue<string>());
        Assert.Equal(typeof(ImeManagerSurfaceFactory).FullName, surface["type"]!.GetValue<string>());
        Assert.Equal("stdio-jsonrpc", manifest["runtime"]!["transport"]!.GetValue<string>());
        Assert.EndsWith(
            "ImeManager.Runtime.exe",
            manifest["runtime"]!["command"]!.GetValue<string>(),
            StringComparison.Ordinal);
        var commandIds = manifest["commands"]!.AsArray()
            .Select(item => item!["id"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ime-manager.health", commandIds);
        Assert.Contains("ime-manager.snapshot", commandIds);
        Assert.Contains("ime-manager.apply", commandIds);
        Assert.Equal(
            "user",
            manifest["permissions"]!.AsArray().Single()!["level"]!.GetValue<string>());

        var projectPath = Path.Combine(ToolRoot, "src", "ImeManager.Tool", "ImeManager.Tool.csproj");
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var project = XDocument.Load(projectPath);
        var packageReferences = project.Descendants("PackageReference")
            .Select(element => (
                Include: element.Attribute("Include")?.Value ?? "",
                Version: element.Attribute("Version")?.Value ?? ""))
            .ToArray();
        Assert.Equal(2, packageReferences.Length);
        Assert.Contains(
            packageReferences,
            reference => reference is { Include: "MyPowerTools.AvaloniaSdk", Version: "0.2.0" });
        Assert.Contains(
            packageReferences,
            reference => reference is { Include: "MyPowerTools.ToolSdk", Version: "0.2.0" });

        var projectReferences = project.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? "")
            .ToArray();
        Assert.Single(projectReferences);
        var suiteSourcePrefix = Path.GetFullPath(Path.Combine(Root, "src")) + Path.DirectorySeparatorChar;
        Assert.DoesNotContain(
            projectReferences,
            reference => Path.GetFullPath(Path.Combine(projectDirectory, reference))
                .StartsWith(suiteSourcePrefix, StringComparison.OrdinalIgnoreCase));

        var build = File.ReadAllText(Path.Combine(Root, "tools", "ime-manager", "build.ps1"));
        Assert.Contains("validate", build, StringComparison.Ordinal);
        Assert.Contains("pack", build, StringComparison.Ordinal);
        Assert.Contains("ImeManager.Runtime.exe", build, StringComparison.Ordinal);
        Assert.Contains("configuration built by this invocation", build, StringComparison.Ordinal);
        Assert.Contains("systemMutation = 'sidecar-required'", build, StringComparison.Ordinal);
        Assert.Contains("elevatedWrite = 'broker-required'", build, StringComparison.Ordinal);
    }

    private static InputMethodSnapshot SampleSnapshot()
    {
        var chineseKeyboard = new InputMethodInfo(
            "0804:00000804",
            0x0804,
            "中文(简体)",
            "中文(简体) - 美式键盘",
            InputMethodKind.KeyboardLayout,
            true,
            true,
            Guid.Empty,
            Guid.Empty,
            0x00000804);
        var usKeyboard = new InputMethodInfo(
            "0409:00000409",
            0x0409,
            "英语(美国)",
            "美式键盘",
            InputMethodKind.KeyboardLayout,
            true,
            false,
            Guid.Empty,
            Guid.Empty,
            0x00000409);
        var microsoftPinyin = new InputMethodInfo(
            "0804:{A028AE76-01B1-46C2-99C4-ACD9858AE02F}{B5FE1F02-D5F2-4445-9C03-C568F23C99A1}",
            0x0804,
            "中文(简体)",
            "微软拼音",
            InputMethodKind.TextService,
            false,
            false,
            Guid.Parse("A028AE76-01B1-46C2-99C4-ACD9858AE02F"),
            Guid.Parse("B5FE1F02-D5F2-4445-9C03-C568F23C99A1"),
            0);
        return new InputMethodSnapshot(
            "windows",
            [chineseKeyboard, usKeyboard],
            [microsoftPinyin],
            chineseKeyboard.TipString,
            SwitchHotkeys.WindowsDefault);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the MyPowerTools repository root.");
    }

    private sealed class FakeInputMethodPlatform : IInputMethodPlatform
    {
        public FakeInputMethodPlatform(InputMethodSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public bool IsSupported => true;
        public InputMethodSnapshot Snapshot { get; private set; }
        public List<string> Enabled { get; } = [];
        public List<string> Disabled { get; } = [];
        public IReadOnlyList<string>? Order { get; private set; }
        public string? DefaultTip { get; private set; }
        public SwitchHotkeys Hotkeys { get; private set; } = SwitchHotkeys.WindowsDefault;
        public bool Notified { get; private set; }

        public InputMethodSnapshot ReadSnapshot(InputMethodReadOptions options) => Snapshot;

        public void Enable(string tipString) => Enabled.Add(tipString);

        public void Disable(string tipString) => Disabled.Add(tipString);

        public void WriteEnabledOrder(IReadOnlyList<string> enabledTipStrings)
        {
            Order = [.. enabledTipStrings];
            var lookup = Snapshot.Enabled.Concat(Snapshot.Available)
                .ToDictionary(item => item.TipString, StringComparer.OrdinalIgnoreCase);
            Snapshot = Snapshot with
            {
                Enabled = enabledTipStrings
                    .Select(tip => lookup[tip] with
                    {
                        IsEnabled = true,
                        IsDefault = string.Equals(tip, DefaultTip ?? Snapshot.DefaultTipString, StringComparison.OrdinalIgnoreCase)
                    })
                    .ToArray(),
                Available = lookup.Values
                    .Where(item => !enabledTipStrings.Contains(item.TipString, StringComparer.OrdinalIgnoreCase))
                    .Select(item => item with { IsEnabled = false, IsDefault = false })
                    .ToArray()
            };
        }

        public void SetDefault(string tipString)
        {
            DefaultTip = tipString;
            Snapshot = Snapshot with
            {
                DefaultTipString = tipString,
                Enabled = Snapshot.Enabled
                    .Select(item => item with
                    {
                        IsDefault = string.Equals(item.TipString, tipString, StringComparison.OrdinalIgnoreCase)
                    })
                    .ToArray()
            };
        }

        public void SetHotkeys(SwitchHotkeys hotkeys)
        {
            Hotkeys = hotkeys;
            Snapshot = Snapshot with { Hotkeys = hotkeys };
        }

        public void NotifyChanged() => Notified = true;
    }
}
