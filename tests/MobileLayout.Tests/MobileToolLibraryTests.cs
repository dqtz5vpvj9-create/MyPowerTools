using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Services.Mobile;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

namespace MobileLayout.Tests;

/// <summary>
/// The phone tool library must cover the delivery list, group it by 用途, split 手机/电脑 honestly and
/// persist favorites through the one shared preferences store.
/// </summary>
public sealed partial class MobileToolLibraryTests
{
    private static readonly string[] PrototypeDeliveryToolIds =
    [
        "file-transfer", "remote-notifications", "remote-commands",
        "paste-image", "input-monitor", "screenease", "ime-manager",
        "local-lag-cleaner", "nssm-manager", "smartbird-thermostat",
        "adb-forwarder", "doubao-agent"
    ];

    [Fact]
    public async Task Library_covers_the_delivery_tools_and_groups_them_by_purpose()
    {
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var catalog = new MobileToolCatalogService(MobileTestEnvironment.CreateServices().ToolProducts);

        var library = await catalog.LoadAsync();

        foreach (var toolId in PrototypeDeliveryToolIds)
        {
            Assert.Contains(library.Entries, entry => entry.ToolId == toolId);
        }

        Assert.Equal(["连接与协作", "日常效率", "设备维护", "开发与自动化"], library.Groups);
        Assert.Equal(3, library.InGroup("连接与协作").Count);
        Assert.Equal(4, library.InGroup("日常效率").Count);
        Assert.True(library.InGroup("设备维护").Count >= 3);
        Assert.Equal(2, library.InGroup("开发与自动化").Count);
    }

    [Fact]
    public async Task Phone_and_computer_tools_are_split_by_the_real_catalog_and_never_faked()
    {
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var catalog = new MobileToolCatalogService(MobileTestEnvironment.CreateServices().ToolProducts);

        var library = await catalog.LoadAsync();

        var transfer = library.Entries.First(entry => entry.ToolId == "file-transfer");
        Assert.Equal(MobileToolPlatform.ThisDevice, transfer.Platform);
        Assert.True(transfer.CanOpen);
        Assert.Equal("可用", transfer.StatusLabel);

        var paste = library.Entries.First(entry => entry.ToolId == "paste-image");
        Assert.Equal(MobileToolPlatform.Computer, paste.Platform);
        Assert.False(paste.CanOpen);
        Assert.Equal(MobileToolCatalog.ComputerStatusLabel, paste.StatusLabel);
        Assert.Equal(ToolAvailability.Unavailable, paste.Availability);

        // No computer entry may claim a phone capability or a successful run.
        foreach (var entry in library.Entries.Where(item => item.Platform == MobileToolPlatform.Computer))
        {
            Assert.False(entry.CanOpen, $"{entry.ToolId} claims it can run on the phone");
            Assert.Contains("连接电脑后", entry.StatusDetail);
        }
    }

    [Fact]
    public async Task Unknown_local_tools_still_appear_so_the_library_is_catalog_driven()
    {
        var descriptors = TestToolHost.DefaultPhoneCatalog()
            .Append(TestToolHost.PhoneTool("brand-new-tool", "Brand New Tool", "A tool the phone build really registered."))
            .ToArray();
        using var host = new TestToolHost(descriptors);
        var catalog = new MobileToolCatalogService(MobileTestEnvironment.CreateServices().ToolProducts);

        var library = await catalog.LoadAsync();

        var entry = library.Entries.First(item => item.ToolId == "brand-new-tool");
        Assert.Equal("Brand New Tool", entry.Title);
        Assert.Equal("A tool the phone build really registered.", entry.Description);
        Assert.Equal(MobileToolPlatform.ThisDevice, entry.Platform);
        Assert.True(entry.CanOpen);
    }

    [Fact]
    public async Task Computer_tool_detail_states_the_required_device_and_offers_no_run_action()
    {
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var catalog = new MobileToolCatalogService(MobileTestEnvironment.CreateServices().ToolProducts);
        var library = await catalog.LoadAsync();
        var navigator = new RecordingNavigator();
        var item = new MobileToolItemViewModel(library.Entries.First(entry => entry.ToolId == "adb-forwarder"), navigator);

        var detail = new MobileToolDetailViewModel(item, navigator);

        Assert.True(detail.IsComputerTool);
        Assert.False(detail.CanOpen);
        Assert.False(detail.HasControlDevice);
        Assert.Equal("需要一台电脑", detail.RequirementTitle);
        Assert.Contains("先连接电脑", detail.RequirementDetail);
        Assert.DoesNotContain("运行完成", detail.RequirementDetail);
        Assert.DoesNotContain("已送达", detail.StatusDetail);
        Assert.Equal("需要电脑", detail.StatusLabel);

        // With a real imported computer the same page offers that computer's control entry.
        var withComputer = new MobileToolDetailViewModel(
            item,
            navigator,
            FakeMobileControlDeviceService.Device("grant-1", "工作电脑", "reachable", "今天 09:12 应答"));
        Assert.True(withComputer.HasControlDevice);
        Assert.Equal("在 工作电脑 上运行", withComputer.RequirementTitle);
        Assert.Equal("Windows · 已连接", withComputer.ControlDeviceState);
        Assert.True(withComputer.OpenOnComputerCommand.CanExecute(null));

        withComputer.OpenOnComputerCommand.Execute(null);
        MobileToolLibraryTests_PumpUntil(() => navigator.Activations.Count == 1);
        Assert.Equal("mobile-tool-control", navigator.Activations[0].ToolId);
        Assert.Contains("device=grant-1", navigator.Activations[0].Uri);
        Assert.Contains("tool=adb-forwarder", navigator.Activations[0].Uri);
    }

    [Fact]
    public async Task Search_matches_titles_descriptions_and_keywords()
    {
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var tools = await CreateToolsAsync();

        tools.SearchText = "通知";
        Assert.Contains(Items(tools), item => item.ToolId == "remote-notifications");
        Assert.DoesNotContain(Items(tools), item => item.ToolId == "local-lag-cleaner");

        tools.SearchText = "文件";
        Assert.Contains(Items(tools), item => item.ToolId == "file-transfer");

        tools.SearchText = "电脑";
        Assert.Contains(Items(tools), item => item.ToolId == "doubao-agent");
        Assert.DoesNotContain(Items(tools), item => item.ToolId == "remote-commands");

        tools.SearchText = "这个词不存在";
        Assert.True(tools.IsEmpty);
        Assert.Equal("换个词试试", tools.EmptyTitle);

        await ((AsyncRelayCommand)tools.ClearSearchCommand).ExecuteAsync(null);
        Assert.False(tools.IsEmpty);
    }

    [Fact]
    public async Task Filters_split_phone_computer_and_favorite_tools()
    {
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var tools = await CreateToolsAsync();

        await tools.SelectFilterAsync(MobileToolsViewModel.FilterThisDevice);
        Assert.All(Items(tools), item => Assert.False(item.IsComputerTool));
        Assert.Contains(Items(tools), item => item.ToolId == "remote-commands");

        await tools.SelectFilterAsync(MobileToolsViewModel.FilterComputer);
        Assert.All(Items(tools), item => Assert.True(item.IsComputerTool));
        Assert.DoesNotContain(Items(tools), item => item.ToolId == "remote-commands");

        await tools.SelectFilterAsync(MobileToolsViewModel.FilterFavorites);
        Assert.True(tools.IsEmpty);
        Assert.Equal("还没有常用工具", tools.EmptyTitle);

        await tools.SelectFilterAsync(MobileToolsViewModel.FilterAll);
        var favorite = tools.Find("paste-image")!;
        await favorite.ToggleFavoriteAsync();

        await tools.SelectFilterAsync(MobileToolsViewModel.FilterFavorites);
        Assert.Single(Items(tools));
        Assert.Equal("paste-image", Items(tools)[0].ToolId);
    }

    [Fact]
    public async Task Favorite_state_survives_a_reload_through_the_shared_preferences_store()
    {
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var root = Path.Combine(AppContext.BaseDirectory, "mobile-test-state", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "tool-preferences.json");
        var products = new ShellToolProductService(new ShellToolPreferencesStore(path));
        var catalog = new MobileToolCatalogService(products);
        var navigator = new RecordingNavigator();

        var library = await catalog.LoadAsync();
        var item = new MobileToolItemViewModel(library.Entries.First(entry => entry.ToolId == "paste-image"), navigator);
        await item.ToggleFavoriteAsync();
        Assert.True(item.IsFavorite);
        Assert.Contains(navigator.Toasts, toast => toast.Contains("已加入常用", StringComparison.Ordinal));

        // A fresh service over the same file sees the persisted favorite, including for a tool the
        // phone catalog does not install.
        var reloaded = new ShellToolProductService(new ShellToolPreferencesStore(path));
        var reloadedLibrary = await new MobileToolCatalogService(reloaded).LoadAsync();
        Assert.True(reloadedLibrary.Entries.First(entry => entry.ToolId == "paste-image").Card!.IsFavorite);

        var remove = new MobileToolItemViewModel(reloadedLibrary.Entries.First(entry => entry.ToolId == "paste-image"), navigator);
        await remove.ToggleFavoriteAsync();
        Assert.False(remove.IsFavorite);
        Assert.DoesNotContain(
            "paste-image",
            new ShellToolPreferencesStore(path).Current.FavoriteToolIds);
    }

    private static async Task<MobileToolsViewModel> CreateToolsAsync()
    {
        var services = MobileTestEnvironment.CreateServices();
        var tools = new MobileToolsViewModel(
            new MobileShellData(new MobileToolCatalogService(services.ToolProducts), services.Devices),
            new RecordingNavigator());
        await tools.LoadAsync();
        return tools;
    }

    /// <summary>Spins until the command's continuation ran (AsyncRelayCommand executes detached).</summary>
    private static void MobileToolLibraryTests_PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }

        Assert.True(condition(), "the command never completed");
    }

    private static List<MobileToolItemViewModel> Items(MobileToolsViewModel tools) =>
        tools.Groups.SelectMany(group => group.Items).ToList();

    private sealed class RecordingNavigator : IMobileNavigator
    {
        public List<string> OpenedTools { get; } = [];
        public List<string> Pages { get; } = [];
        public List<string> Sheets { get; } = [];
        public List<string> Toasts { get; } = [];

        public List<(string ToolId, string RouteId, string Uri)> Activations { get; } = [];

        public Task OpenToolSurfaceAsync(string toolId)
        {
            OpenedTools.Add(toolId);
            return Task.CompletedTask;
        }

        public Task ActivateToolAsync(string toolId, string routeId, string activationUri)
        {
            Activations.Add((toolId, routeId, activationUri));
            return Task.CompletedTask;
        }

        public Task ShowToolDetailAsync(string toolId)
        {
            Pages.Add($"tool:{toolId}");
            return Task.CompletedTask;
        }

        public Task ShowDeviceDetailAsync(string deviceId)
        {
            Pages.Add($"device:{deviceId}");
            return Task.CompletedTask;
        }

        public Task ShowPageAsync(string pageKey)
        {
            Pages.Add(pageKey);
            return Task.CompletedTask;
        }

        public Task ShowRootPageAsync(string pageKey)
        {
            Pages.Add(pageKey);
            return Task.CompletedTask;
        }

        public Task GoBackPageAsync() => Task.CompletedTask;

        public Task ShowSheetAsync(string sheetKey, string? argument = null)
        {
            Sheets.Add(sheetKey);
            return Task.CompletedTask;
        }

        public Task CloseSheetAsync() => Task.CompletedTask;

        public void ShowToast(string message) => Toasts.Add(message);

        public Task RefreshAsync() => Task.CompletedTask;
    }
}
