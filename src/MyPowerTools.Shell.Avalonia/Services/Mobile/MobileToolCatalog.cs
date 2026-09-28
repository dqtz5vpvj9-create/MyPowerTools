using MyPowerTools.Shell.Avalonia.ViewModels;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services.Mobile;

/// <summary>Where a tool actually runs according to the catalogs this shell can reach.</summary>
public enum MobileToolPlatform
{
    /// <summary>The product has a phone implementation; availability determines whether it can open.</summary>
    ThisDevice,

    /// <summary>A known MPT desktop tool that no reachable catalog offers to this phone.</summary>
    Computer,

    /// <summary>No executable implementation or supported remote destination is known.</summary>
    Unavailable
}

/// <summary>
/// One tool library row. Every state field comes from the live catalog (or from the honest
/// "needs a computer" classification); the phone never invents a run state.
/// </summary>
public sealed record MobileToolEntry(
    string ToolId,
    string Title,
    string Description,
    string Group,
    string IconGlyph,
    string Keywords,
    MobileToolPlatform Platform,
    ToolAvailability Availability,
    string StatusLabel,
    string StatusDetail,
    bool CanOpen,
    ToolCardViewModel? Card)
{
    // ToolId stays as the product-key alias used by existing phone navigation and favorites.
    public string ProductId => ToolId;
    public string ImplementationId { get; init; } = ToolId;
    public MobileToolPlatform ExecutionLocation => Platform;
    public string OpenUnavailableReason => CanOpen ? "" : StatusDetail;
    public string RuntimeState { get; init; } = "";
    public string DiagnosticDetail { get; init; } = "";
}

/// <summary>The tool library as the phone presents it, in prototype group order.</summary>
public sealed record MobileToolLibrary(IReadOnlyList<MobileToolEntry> Entries)
{
    private static readonly string[] GroupOrder =
    [
        MobileToolCatalog.GroupConnection,
        MobileToolCatalog.GroupEveryday,
        MobileToolCatalog.GroupMaintenance,
        MobileToolCatalog.GroupAutomation,
        MobileToolCatalog.GroupOther
    ];

    public IReadOnlyList<string> Groups => Entries
        .Select(entry => entry.Group)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(group => Array.IndexOf(GroupOrder, group) is var index && index >= 0 ? index : int.MaxValue)
        .ThenBy(group => group, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<MobileToolEntry> InGroup(string group) => Entries
        .Where(entry => string.Equals(entry.Group, group, StringComparison.Ordinal))
        .ToArray();

    public IReadOnlyList<string> RecentToolIds(IReadOnlyList<string> mostRecentFirst) => mostRecentFirst
        .Select(ToolProductIdentity.ProductId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(id => Entries.Any(entry => string.Equals(entry.ToolId, id, StringComparison.OrdinalIgnoreCase)))
        .ToArray();
}

/// <summary>
/// Phone presentation of the real tool catalog: Chinese names, 用途分组, the twelve delivery-list
/// tools and their phone/computer split. Titles and descriptions are the shipped product ones from
/// each tool manifest; availability always comes from the runtime catalog.
/// </summary>
public static class MobileToolCatalog
{
    public const string GroupConnection = "连接与协作";
    public const string GroupEveryday = "日常效率";
    public const string GroupMaintenance = "设备维护";
    public const string GroupAutomation = "开发与自动化";
    public const string GroupOther = "其他工具";

    /// <summary>A tool that only runs on a paired computer; the phone cannot load a local surface.</summary>
    public const string ComputerStatusLabel = "需要电脑";
    public const string ComputerStatusDetail =
        "此工具在电脑上运行。连接电脑后可以从这里打开它；没有连接时不会执行任何操作。";

    private sealed record Definition(
        string ToolId,
        string Title,
        string Description,
        string Group,
        string IconGlyph,
        MobileToolPlatform Platform,
        string Keywords,
        string Category);

    // These are product definitions, not module ids. Computer execution stays on the computer even
    // when its metadata is bundled with the phone or its commands are reachable through the gateway.
    private static readonly Definition[] Definitions =
    [
        new("file-transfer", "文件助手", "文字和文件，在设备间传递", GroupConnection, "\u21C4", MobileToolPlatform.ThisDevice, "文件 发送 传输 互传 分享 file transfer", "Files"),
        new("remote-notifications", "远程通知", "重要消息，随身带走", GroupConnection, "\u25CF", MobileToolPlatform.ThisDevice, "通知 消息 提醒 notifications", "Notifications"),
        new("remote-commands", "远程命令", "常用命令，点一下就好", GroupConnection, "\u203A", MobileToolPlatform.ThisDevice, "命令 终端 ssh 执行 commands", "Commands"),
        new("paste-image", "图片快传", "上传剪贴板图片，接着粘贴", GroupEveryday, "\u25A3", MobileToolPlatform.Computer, "图片 剪贴板 粘贴 paste image", "Clipboard"),
        new("input-monitor", "输入监测", "查看使用习惯，记得适时休息", GroupEveryday, "\u25CE", MobileToolPlatform.Computer, "输入 键盘 鼠标 休息 习惯 input", "Wellbeing"),
        new("screenease", "屏幕舒适", "让屏幕适合此刻的光线", GroupEveryday, "\u2600", MobileToolPlatform.Computer, "屏幕 亮度 色温 护眼 screenease", "Display"),
        new("ime-manager", "输入法管理", "每个应用，用对输入法", GroupEveryday, "\u2328", MobileToolPlatform.Computer, "输入法 键盘 切换 ime", "System"),
        new("local-lag-cleaner", "卡顿清理", "处理影响流畅度的问题", GroupMaintenance, "\u2726", MobileToolPlatform.Computer, "卡顿 清理 优化 性能 lag", "System"),
        new("nssm-manager", "服务管理", "查看和管理 Windows 服务", GroupMaintenance, "\u2637", MobileToolPlatform.Computer, "服务 windows nssm 后台", "System"),
        new("smartbird-thermostat", "温度管理", "散热器、露点与温度趋势", GroupMaintenance, "\u2668", MobileToolPlatform.Computer, "温度 散热 露点 风扇 smartbird", "设备控制"),
        new("adb-forwarder", "ADB 转发", "连接你的开发设备", GroupAutomation, "\u2318", MobileToolPlatform.Computer, "adb 转发 开发 调试 设备", "Device & Network"),
        new("doubao-agent", "豆包电脑助手", "让电脑帮你完成操作", GroupAutomation, "\u25C6", MobileToolPlatform.Computer, "豆包 助手 自动化 电脑任务 doubao", "自动化"),
        new("process-monitor", "进程监测", "查看电脑上的进程状态", GroupMaintenance, "\u25A6", MobileToolPlatform.Computer, "进程 状态 监测 process", "Diagnostics")
    ];

    private static readonly Dictionary<string, Definition> ById =
        Definitions.ToDictionary(definition => definition.ToolId, StringComparer.OrdinalIgnoreCase);

    /// <summary>The twelve delivery-list tools plus the process monitor, in prototype order.</summary>
    public static IReadOnlyList<string> KnownToolIds { get; } = Definitions.Select(item => item.ToolId).ToArray();

    public static MobileToolEntry FromLocalCard(ToolCardViewModel card, HostProto.ToolDescriptor? descriptor = null)
    {
        var productId = ToolProductIdentity.ProductId(card.ToolId);
        var definition = ById.GetValueOrDefault(productId);
        if (definition?.Platform == MobileToolPlatform.Computer)
            return FromComputerCard(card) with { RuntimeState = descriptor?.State ?? "", DiagnosticDetail = descriptor?.StateSummary ?? "" };

        var state = descriptor?.State.ToLowerInvariant() ?? card.StatusLabel.ToLowerInvariant();
        var unavailable = LocalUnavailableReason(card, descriptor);
        var canOpen = card.CanOpen && unavailable.Length == 0;
        var status = LocalStatus(state, card.Availability, canOpen, unavailable);
        return new MobileToolEntry(
            productId,
            definition?.Title ?? card.Title,
            definition?.Description ?? card.Description,
            definition?.Group ?? GroupForCategory(card.Category),
            definition?.IconGlyph ?? card.IconGlyph,
            definition?.Keywords ?? $"{card.Title} {card.Description} {card.Category}",
            definition is null && state == "unsupported" ? MobileToolPlatform.Unavailable : MobileToolPlatform.ThisDevice,
            canOpen ? card.Availability : card.Availability == ToolAvailability.Available ? ToolAvailability.Unavailable : card.Availability,
            status.Label,
            status.Detail,
            canOpen,
            card)
        {
            ImplementationId = card.ToolId,
            RuntimeState = state,
            DiagnosticDetail = descriptor?.StateSummary ?? card.StatusDetail
        };
    }

    /// <summary>
    /// A known desktop tool that the phone catalog does not offer. The card still comes from the
    /// shared product service so favorites read and write the one tool preferences store.
    /// </summary>
    public static MobileToolEntry FromComputerCard(ToolCardViewModel card)
    {
        var productId = ToolProductIdentity.ProductId(card.ToolId);
        var definition = ById[productId];
        return new MobileToolEntry(
            productId,
            definition.Title,
            definition.Description,
            definition.Group,
            definition.IconGlyph,
            definition.Keywords,
            MobileToolPlatform.Computer,
            ToolAvailability.Unavailable,
            ComputerStatusLabel,
            ComputerReason(productId),
            CanOpen: false,
            card) { ImplementationId = card.ToolId };
    }

    /// <summary>Preserves known products absent from this build without inventing a runnable module.</summary>
    public static HostProto.ToolDescriptor BuildComputerToolDescriptor(string toolId)
    {
        var definition = ById[toolId];
        return new HostProto.ToolDescriptor
        {
            ToolId = definition.ToolId,
            OwnerModuleId = definition.ToolId,
            Title = definition.Title,
            Description = definition.Description,
            Category = definition.Category,
            Icon = $"tool.{definition.ToolId}",
            PrimaryRouteId = "main",
            Availability = "unavailable",
            State = definition.Platform == MobileToolPlatform.Computer ? "unsupported" : "missing",
            ToolType = "dotnet-surface",
            StateSummary = definition.Platform == MobileToolPlatform.Computer
                ? ComputerReason(definition.ToolId) : "手机组件未包含在当前工具目录中。"
        };
    }

    public static bool IsKnownTool(string toolId) => ById.ContainsKey(ToolProductIdentity.ProductId(toolId));

    public static bool IsComputerProduct(string toolId) =>
        ById.GetValueOrDefault(ToolProductIdentity.ProductId(toolId))?.Platform == MobileToolPlatform.Computer;

    public static string GroupForCategory(string category) => category?.Trim() switch
    {
        "Files" or "Clipboard" => GroupConnection,
        "Notifications" or "Commands" or "Communication" => GroupConnection,
        "Wellbeing" or "Display" => GroupEveryday,
        "Device & Network" or "System" or "设备控制" or "Diagnostics" => GroupMaintenance,
        "自动化" or "Automation" => GroupAutomation,
        _ => GroupOther
    };

    private static string ComputerReason(string productId) => productId switch
    {
        "process-monitor" => "查看的是电脑上的进程。连接电脑后，选择已授权的电脑查看；手机不会读取其他应用的进程。",
        "input-monitor" => "键盘、鼠标和使用统计来自电脑。连接电脑后查看该电脑允许访问的统计与设置。",
        "nssm-manager" => "此工具管理 Windows 服务。连接电脑后选择已授权的 Windows 电脑操作。",
        "ime-manager" => "此工具管理电脑输入法。连接电脑后调整该电脑的输入法设置，不会更改手机键盘。",
        "screenease" => "亮度与色温在电脑上调整。连接电脑后操作该电脑支持的显示设置。",
        "adb-forwarder" => "ADB 与端口映射在电脑上执行。连接电脑后管理该电脑的设备连接。",
        "smartbird-thermostat" => "温度设备由电脑上的服务管理。连接电脑后查看和控制已配置的设备。",
        "local-lag-cleaner" => "扫描与清理在目标电脑上执行。连接电脑后先查看诊断，再确认需要的操作。",
        "doubao-agent" => "任务使用电脑屏幕并在电脑上执行。连接电脑后选择目标并查看任务进度。",
        "paste-image" => "当前上传实现使用电脑剪贴板。连接电脑后使用电脑上已配置的上传功能。",
        _ => ComputerStatusDetail
    };

    private static string LocalUnavailableReason(ToolCardViewModel card, HostProto.ToolDescriptor? descriptor)
    {
        var state = descriptor?.State.ToLowerInvariant();
        if (state == "missing") return "此工具的手机组件尚未安装，请更新应用后重试。";
        if (state == "unsupported") return "此设备暂不支持这个工具，现有记录和收藏会保留。";
        if (state == "disabled") return "此工具已停用，可在工具设置中重新启用。";
        if (card.IsPaused) return "此工具已暂停提供，现有记录和收藏会保留。";
        if (card.IsInDevelopment) return "此工具的手机界面尚未提供。";
        if (!card.CanOpen) return "此工具暂时无法打开，请刷新工具列表后重试。";
        if (descriptor is null) return "";

        var route = descriptor.Routes.FirstOrDefault(item => item.RouteId == descriptor.PrimaryRouteId)
            ?? descriptor.Routes.FirstOrDefault();
        if (route is null)
            return descriptor.ToolType == "headless-tool" && descriptor.Commands.Count > 0
                ? "" : "此工具尚未提供可打开的界面。";
        if (descriptor.ToolType == "dotnet-surface" || route.SurfaceKind == "dotnet")
        {
            if (string.IsNullOrWhiteSpace(route.Assembly) || string.IsNullOrWhiteSpace(route.Type) ||
                !File.Exists(Path.Combine(descriptor.SourceDirectory, route.Assembly)))
                return "此工具的手机组件尚未安装完整，请更新应用后重试。";
        }
        return "";
    }

    private static (string Label, string Detail) LocalStatus(string state, ToolAvailability availability, bool canOpen, string reason)
    {
        if (!canOpen) return (state switch
        {
            "disabled" => "已停用",
            "unsupported" => "此设备暂不支持",
            "missing" => "尚未安装",
            _ when availability == ToolAvailability.InDevelopment => "尚未提供",
            _ when availability == ToolAvailability.Paused => "已暂停",
            _ => "暂不可用"
        }, reason);
        return state switch
        {
            "ready" or "indexed" or "idle" or "stopped" => ("可用", "在这台手机上打开并使用。"),
            "running" => ("运行中", "工具正在这台手机上运行。"),
            "connected" => ("已连接", "工具已连接，可以打开查看。"),
            "starting" => ("正在启动", "工具正在准备，打开后可查看状态。"),
            "degraded" or "error" or "failed" or "needs attention" => ("需要处理", "工具需要检查，打开后查看设置与具体提示。"),
            _ => ("状态暂不可用", "暂时无法读取运行状态，可以打开工具查看。")
        };
    }
}

/// <summary>Loads the phone tool library from the live catalog and the shared preferences store.</summary>
public sealed class MobileToolCatalogService
{
    private readonly ShellToolProductService _products;

    public MobileToolCatalogService(ShellToolProductService products)
    {
        _products = products ?? throw new ArgumentNullException(nameof(products));
    }

    public async Task<MobileToolLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = await _products.LoadToolDescriptorsAsync(cancellationToken).ConfigureAwait(true);
        // The open delegate is deliberately inert: the mobile library owns navigation and calls the
        // workspace's tool-surface loader itself, so the card is used for identity, availability and
        // the favorite command only.
        var selected = descriptors.GroupBy(tool => ToolProductIdentity.ProductId(tool.ToolId), StringComparer.OrdinalIgnoreCase)
            // A shipped phone implementation wins even when disabled: the desktop alias must not
            // become a second row or accidentally bypass the user's disabled state.
            .Select(group => group.OrderByDescending(tool => tool.ToolId.EndsWith("-android", StringComparison.OrdinalIgnoreCase)).First())
            .ToArray();
        var cards = _products.BuildToolCards(selected, static _ => Task.CompletedTask, null, includePaused: true);
        var byImplementation = selected.ToDictionary(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase);
        var localIds = cards
            .Select(card => ToolProductIdentity.ProductId(card.ToolId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = new List<MobileToolEntry>(cards.Count + MobileToolCatalog.KnownToolIds.Count);
        entries.AddRange(cards.Select(card => MobileToolCatalog.FromLocalCard(card, byImplementation[card.ToolId])));

        var missing = MobileToolCatalog.KnownToolIds
            .Where(id => !localIds.Contains(id))
            .Select(MobileToolCatalog.BuildComputerToolDescriptor)
            .ToArray();
        if (missing.Length > 0)
        {
            var computerCards = _products.BuildToolCards(missing, static _ => Task.CompletedTask, null);
            entries.AddRange(computerCards.Select(card => MobileToolCatalog.IsComputerProduct(card.ToolId)
                ? MobileToolCatalog.FromComputerCard(card)
                : MobileToolCatalog.FromLocalCard(card, missing.First(tool => tool.ToolId == card.ToolId))));
        }

        return new MobileToolLibrary(entries);
    }

    /// <summary>Most recently opened tools that are still part of the catalog, newest first.</summary>
    public IReadOnlyList<MobileToolEntry> RecentEntries(MobileToolLibrary library, int take)
    {
        ArgumentNullException.ThrowIfNull(library);
        var cards = library.Entries
            .Select(entry => entry.Card)
            .Where(card => card is not null)
            .Select(card => card!)
            .ToArray();
        if (cards.Length == 0)
        {
            return [];
        }

        var byId = library.Entries.ToDictionary(entry => entry.ToolId, StringComparer.OrdinalIgnoreCase);
        return _products.RecentTools(cards)
            .Where(card => byId.ContainsKey(ToolProductIdentity.ProductId(card.ToolId)))
            .Select(card => byId[ToolProductIdentity.ProductId(card.ToolId)])
            .Take(take)
            .ToArray();
    }
}
