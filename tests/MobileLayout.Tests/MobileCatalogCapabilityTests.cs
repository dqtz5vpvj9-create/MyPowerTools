using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.Services.Mobile;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;
using MyPowerTools.Shell.Avalonia.Views.Mobile;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MobileLayout.Tests;

public sealed partial class MobileToolLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Directory.Exists("/mnt/cache/data-cache")
        ? "/mnt/cache/data-cache" : Path.GetTempPath(), "mpt-catalog-tests", Guid.NewGuid().ToString("N"));
    private string PreferencesPath => Path.Combine(_root, "tool-preferences.json");
    private ShellToolProductService Products() => new(new ShellToolPreferencesStore(PreferencesPath));

    [Fact]
    public async Task Android_aliases_merge_with_desktop_products_and_open_the_installed_implementation()
    {
        var desktopNotifications = TestToolHost.PhoneTool("remote-notifications", "Old notifications");
        desktopNotifications.State = "unsupported";
        using var host = new TestToolHost([.. TestToolHost.DefaultPhoneCatalog(), desktopNotifications]);
        var library = await new MobileToolCatalogService(Products()).LoadAsync();
        var entry = Assert.Single(library.Entries, item => item.ProductId == "remote-notifications");
        Assert.Equal("remote-notifications-android", entry.ImplementationId);
        Assert.Equal("远程通知", entry.Title);
        Assert.True(entry.CanOpen);
        var navigator = new Navigator();
        var item = new MobileToolItemViewModel(entry, navigator);
        await ((AsyncRelayCommand)item.OpenCommand).ExecuteAsync(null);
        Assert.Equal(["remote-notifications-android"], navigator.Opened);
        Assert.Single(library.Entries, item => item.ProductId == "remote-commands");
    }

    [Fact]
    public async Task Legacy_favorites_and_recents_survive_implementation_changes_and_toggle_once()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(PreferencesPath, JsonSerializer.Serialize(new
        {
            favoriteToolIds = new[] { "remote-notifications-android", "remote-notifications", "custom-tool" },
            recentToolIds = new[] { "remote-notifications-android", "remote-notifications", "remote-commands-android" }
        }));
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var products = Products();
        var catalog = new MobileToolCatalogService(products);
        var library = await catalog.LoadAsync();
        var entry = Assert.Single(library.Entries, item => item.ProductId == "remote-notifications");
        Assert.True(entry.Card!.IsFavorite);
        Assert.Equal(["remote-notifications", "remote-commands"], catalog.RecentEntries(library, 10).Select(item => item.ProductId));
        var item = new MobileToolItemViewModel(entry, new Navigator());
        await item.ToggleFavoriteAsync();
        var reloaded = new ShellToolPreferencesStore(PreferencesPath);
        Assert.Equal(["custom-tool"], reloaded.Current.FavoriteToolIds);
        await item.ToggleFavoriteAsync();
        await products.RecordOpenedAsync("remote-notifications-android");
        var persisted = JsonSerializer.Deserialize<ShellToolPreferences>(await File.ReadAllTextAsync(PreferencesPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(["custom-tool", "remote-notifications"], persisted.FavoriteToolIds);
        Assert.Equal(["remote-notifications", "remote-commands"], persisted.RecentToolIds);
    }

    [Fact]
    public async Task Bundled_desktop_metadata_never_becomes_a_local_phone_tool()
    {
        var ids = new[] { "paste-image", "input-monitor", "screenease", "ime-manager", "local-lag-cleaner", "nssm-manager", "smartbird-thermostat", "adb-forwarder", "doubao-agent", "process-monitor" };
        using var host = new TestToolHost(ids.Select(id =>
        {
            var descriptor = TestToolHost.PhoneTool(id, id);
            descriptor.State = "unsupported";
            return descriptor;
        }).ToArray());
        var library = await new MobileToolCatalogService(Products()).LoadAsync();
        foreach (var id in ids)
        {
            var entry = Assert.Single(library.Entries, item => item.ProductId == id);
            Assert.Equal(MobileToolPlatform.Computer, entry.ExecutionLocation);
            Assert.False(entry.CanOpen);
            Assert.Equal("需要电脑", entry.StatusLabel);
            Assert.Contains("连接电脑后", entry.OpenUnavailableReason);
            var item = new MobileToolItemViewModel(entry, new Navigator());
            Assert.False(item.OpenCommand.CanExecute(null));
        }
    }

    [Theory]
    [InlineData("unsupported", "此设备暂不支持")]
    [InlineData("disabled", "已停用")]
    public async Task Non_runnable_phone_implementation_remains_visible_but_cannot_open(string state, string label)
    {
        var phone = TestToolHost.PhoneTool("remote-commands-android", "Remote Commands");
        phone.State = state;
        phone.StateSummary = "Internal transport stack must not appear in the product description";
        using var host = new TestToolHost(phone, TestToolHost.PhoneTool("remote-commands", "Desktop alias"));
        var library = await new MobileToolCatalogService(Products()).LoadAsync();
        var entry = Assert.Single(library.Entries, item => item.ProductId == "remote-commands");
        Assert.Equal("remote-commands-android", entry.ImplementationId);
        Assert.False(entry.CanOpen);
        Assert.Equal(label, entry.StatusLabel);
        Assert.DoesNotContain("Internal", entry.StatusDetail);
        Assert.Equal(phone.StateSummary, entry.DiagnosticDetail);
        Assert.False(entry.Card!.OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task Missing_surface_is_not_confused_with_an_indexed_runnable_module()
    {
        var tool = TestToolHost.PhoneTool("custom-tool", "Custom");
        tool.ToolType = "dotnet-surface";
        tool.State = "indexed";
        tool.SourceDirectory = _root;
        tool.Routes[0].SurfaceKind = "dotnet";
        tool.Routes[0].Assembly = "missing.dll";
        tool.Routes[0].Type = "Custom.Factory";
        using var host = new TestToolHost(tool);
        var library = await new MobileToolCatalogService(Products()).LoadAsync();
        var entry = Assert.Single(library.Entries, item => item.ProductId == "custom-tool");
        Assert.False(entry.CanOpen);
        Assert.Contains("组件尚未安装完整", entry.OpenUnavailableReason);
    }

    [Fact]
    public async Task Unrecognized_unsupported_metadata_is_visible_without_inventing_an_execution_device()
    {
        var tool = TestToolHost.PhoneTool("new-desktop-tool", "New desktop tool");
        tool.State = "unsupported";
        using var host = new TestToolHost(tool);
        var entry = (await new MobileToolCatalogService(Products()).LoadAsync()).Entries.Single(item => item.ProductId == tool.ToolId);
        Assert.Equal(MobileToolPlatform.Unavailable, entry.ExecutionLocation);
        var item = new MobileToolItemViewModel(entry, new Navigator());
        Assert.False(item.CanOpen);
        Assert.Equal("待适配", item.PlatformLabel);
        Assert.Equal("此设备暂不可用", new MobileToolDetailViewModel(item, new Navigator()).RequirementTitle);
    }

    [Theory]
    [InlineData("indexed", "可用")]
    [InlineData("running", "运行中")]
    [InlineData("stopped", "可用")]
    [InlineData("degraded", "需要处理")]
    [InlineData("future-internal-state", "状态暂不可用")]
    public async Task Catalog_states_are_translated_without_claiming_a_recent_task_succeeded(string state, string label)
    {
        var tool = TestToolHost.PhoneTool("file-transfer", "File transfer");
        tool.State = state;
        using var host = new TestToolHost(tool);
        var entry = (await new MobileToolCatalogService(Products()).LoadAsync()).Entries.Single(item => item.ProductId == "file-transfer");
        Assert.Equal(label, entry.StatusLabel);
        Assert.True(entry.CanOpen);
        Assert.DoesNotContain("已完成", entry.StatusLabel);
    }

    [Fact]
    public async Task Paused_tools_and_missing_phone_modules_keep_their_own_unavailable_reason()
    {
        var phone = TestToolHost.PhoneTool("remote-commands-android", "Commands");
        phone.Availability = "paused";
        using var host = new TestToolHost(phone);
        var library = await new MobileToolCatalogService(Products()).LoadAsync();
        var paused = library.Entries.Single(item => item.ProductId == "remote-commands");
        Assert.Equal("已暂停", paused.StatusLabel);
        Assert.False(paused.CanOpen);
        var missing = library.Entries.Single(item => item.ProductId == "file-transfer");
        Assert.Equal("尚未安装", missing.StatusLabel);
        Assert.Equal(MobileToolPlatform.ThisDevice, missing.ExecutionLocation);
        Assert.False(missing.CanOpen);
    }

    [Fact]
    public void Shared_product_card_blocks_unsupported_even_when_a_surface_is_declared_available()
    {
        var descriptor = TestToolHost.PhoneTool("any-tool", "Tool");
        descriptor.State = "unsupported";
        var card = ShellToolProductService.ToCard(descriptor, _ => throw new InvalidOperationException("Must not open"), true);
        Assert.Equal(ToolAvailability.Unavailable, card.Availability);
        Assert.False(card.CanOpen);
        Assert.False(card.OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task Home_and_activity_translate_transfer_states_without_turning_unknown_into_success()
    {
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var service = new MobileDeviceService((_, _, _) => Task.FromResult("""
            {"history":[
                {"name":"direct.txt","state":"completed","delivery":"direct","direction":"send"},
                {"name":"relay.txt","state":"completed","delivery":"relay-uploaded","direction":"send"},
                {"name":"unknown.txt","state":"new-internal-state","direction":"send"}
            ]}
            """));
        var data = new MobileShellData(new MobileToolCatalogService(Products()), service);
        var navigator = new Navigator();
        var tools = new MobileToolsViewModel(data, navigator);
        var home = new MobileHomeViewModel(data, tools, navigator);
        var activity = new MobileActivityViewModel(data, tools, navigator);
        await home.LoadAsync();
        await activity.LoadAsync();
        Assert.Contains(activity.Items, item => item.Title == "direct.txt" && item.Detail.Contains("已送达"));
        Assert.Contains(home.Recent, item => item.Title == "relay.txt" && item.Detail.Contains("等待对方接收"));
        Assert.Contains(activity.Items, item => item.Title == "unknown.txt" && item.Detail.Contains("状态暂不可用"));
        Assert.DoesNotContain(activity.Items, item => item.Detail.Contains("send") || item.Detail.Contains("completed") || item.Detail.Contains("new-internal-state"));
        Assert.Equal("unknown", data.Snapshot!.Activities.Single(item => item.Name == "unknown.txt").State);
    }

    [AvaloniaFact]
    public async Task Unavailable_phone_detail_never_offers_computer_pairing_as_a_fix()
    {
        var phone = TestToolHost.PhoneTool("remote-notifications-android", "Notifications");
        phone.State = "unsupported";
        using var host = new TestToolHost(phone);
        var library = await new MobileToolCatalogService(Products()).LoadAsync();
        var navigator = new Navigator();
        var item = new MobileToolItemViewModel(library.Entries.Single(entry => entry.ProductId == "remote-notifications"), navigator);
        var view = new MobileToolDetailView(new MobileToolDetailViewModel(item, navigator), navigator);
        var window = new Window { Width = 360, Height = 700, Content = view };
        window.Show();
        try
        {
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "连接电脑"));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "打开工具"));
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("此设备暂不支持") == true);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Computer_selector_opens_the_explicit_target_and_never_defaults_to_the_first_of_many()
    {
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var entry = (await new MobileToolCatalogService(Products()).LoadAsync()).Entries.Single(item => item.ProductId == "screenease");
        var devices = new[]
        {
            new MobileControlDevice("grant-one", "电脑", "windows", "reachable", "", true),
            new MobileControlDevice("grant-two", "电脑", "macos", "imported", "", true),
            new MobileControlDevice("grant-lost", "旧电脑", "windows", "imported", "", false)
        };
        var navigator = new Navigator();
        var detail = new MobileToolDetailViewModel(new MobileToolItemViewModel(entry, navigator), navigator, controlDevices: devices);
        var view = new MobileToolDetailView(detail, navigator);
        var window = new Window { Width = 320, Height = 700, Content = view };
        window.Show();
        try
        {
            Assert.Null(detail.SelectedControlDevice);
            Assert.False(detail.OpenOnComputerCommand.CanExecute(null));
            var selector = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>());
            Assert.Equal(3, selector.ItemCount);
            Assert.NotEqual(detail.ControlDeviceLabel(devices[0]), detail.ControlDeviceLabel(devices[1]));
            selector.SelectedItem = devices[1];
            Assert.Equal("grant-two", detail.SelectedControlDevice?.DeviceId);
            Assert.True(detail.OpenOnComputerCommand.CanExecute(null));
            await ((AsyncRelayCommand)detail.OpenOnComputerCommand).ExecuteAsync(null);
            var activation = Assert.Single(navigator.Activations);
            Assert.Equal("mobile-tool-control", activation.ToolId);
            Assert.Contains("device=grant-two", activation.Uri);
            Assert.Contains("tool=screenease", activation.Uri);
            selector.SelectedItem = devices[2];
            Assert.False(detail.OpenOnComputerCommand.CanExecute(null));
            Assert.Contains("授权凭据缺失", detail.ControlDeviceState);
        }
        finally { window.Close(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Navigator : IMobileNavigator
    {
        public List<string> Opened { get; } = [];
        public List<(string ToolId, string Uri)> Activations { get; } = [];
        public Task OpenToolSurfaceAsync(string toolId) { Opened.Add(toolId); return Task.CompletedTask; }
        public Task ActivateToolAsync(string toolId, string routeId, string activationUri) { Activations.Add((toolId, activationUri)); return Task.CompletedTask; }
        public Task ShowToolDetailAsync(string toolId) => Task.CompletedTask;
        public Task ShowDeviceDetailAsync(string deviceId) => Task.CompletedTask;
        public Task ShowPageAsync(string pageKey) => Task.CompletedTask;
        public Task ShowRootPageAsync(string pageKey) => Task.CompletedTask;
        public Task GoBackPageAsync() => Task.CompletedTask;
        public Task ShowSheetAsync(string sheetKey, string? argument = null) => Task.CompletedTask;
        public Task CloseSheetAsync() => Task.CompletedTask;
        public void ShowToast(string message) { }
        public Task RefreshAsync() => Task.CompletedTask;
    }
}
