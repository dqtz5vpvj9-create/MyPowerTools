using MyPowerTools.Shell.Avalonia.ViewModels;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services.Mobile;

/// <summary>Where a tool actually runs according to the catalogs this shell can reach.</summary>
public enum MobileToolPlatform
{
    /// <summary>The runtime catalog this phone talks to offers the tool, so its surface can load here.</summary>
    ThisDevice,

    /// <summary>A known MPT desktop tool that no reachable catalog offers to this phone.</summary>
    Computer
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
    ToolCardViewModel? Card);

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

    // The delivery list the approved prototype covers. Desktop entries are declared here because no
    // reachable catalog offers them to the phone yet; when a paired computer publishes a catalog
    // (second phase), the same ids resolve as ThisDevice entries instead and this table only keeps
    // the Chinese presentation.
    private static readonly Definition[] Definitions =
    [
        new("file-transfer", "文件助手", "发给自己，或发送到设备", GroupConnection, "\u21C4", MobileToolPlatform.ThisDevice, "文件 发送 传输 互传 分享 file transfer", "Files"),
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
        new("process-monitor", "进程监测", "查看电脑上的进程状态", GroupMaintenance, "\u25A6", MobileToolPlatform.ThisDevice, "进程 状态 监测 process", "Diagnostics")
    ];

    private static readonly Dictionary<string, Definition> ById =
        Definitions.ToDictionary(definition => definition.ToolId, StringComparer.OrdinalIgnoreCase);

    /// <summary>The twelve delivery-list tools plus the process monitor, in prototype order.</summary>
    public static IReadOnlyList<string> KnownToolIds { get; } = Definitions.Select(item => item.ToolId).ToArray();

    public static MobileToolEntry FromLocalCard(ToolCardViewModel card)
    {
        var definition = ById.GetValueOrDefault(card.ToolId);
        return new MobileToolEntry(
            card.ToolId,
            definition?.Title ?? card.Title,
            definition?.Description ?? card.Description,
            definition?.Group ?? GroupForCategory(card.Category),
            definition?.IconGlyph ?? card.IconGlyph,
            definition?.Keywords ?? $"{card.Title} {card.Description} {card.Category}",
            MobileToolPlatform.ThisDevice,
            card.Availability,
            LocalStatusLabel(card),
            card.StatusDetail,
            card.CanOpen,
            card);
    }

    /// <summary>
    /// A known desktop tool that the phone catalog does not offer. The card still comes from the
    /// shared product service so favorites read and write the one tool preferences store.
    /// </summary>
    public static MobileToolEntry FromComputerCard(ToolCardViewModel card)
    {
        var definition = ById[card.ToolId];
        return new MobileToolEntry(
            card.ToolId,
            definition.Title,
            definition.Description,
            definition.Group,
            definition.IconGlyph,
            definition.Keywords,
            MobileToolPlatform.Computer,
            ToolAvailability.Unavailable,
            ComputerStatusLabel,
            ComputerStatusDetail,
            CanOpen: false,
            card);
    }

    /// <summary>Materializes a catalog card for a known desktop tool that the phone catalog omits.</summary>
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
            Availability = "available",
            State = "ready",
            ToolType = "dotnet-surface",
            StateSummary = ComputerStatusDetail
        };
    }

    public static bool IsKnownTool(string toolId) => ById.ContainsKey(toolId);

    public static string GroupForCategory(string category) => category?.Trim() switch
    {
        "Files" or "Clipboard" => GroupConnection,
        "Notifications" or "Commands" => GroupConnection,
        "Wellbeing" or "Display" => GroupEveryday,
        "Device & Network" or "System" or "设备控制" or "Diagnostics" => GroupMaintenance,
        "自动化" or "Automation" => GroupAutomation,
        _ => GroupOther
    };

    private static string LocalStatusLabel(ToolCardViewModel card)
    {
        if (card.IsAvailable)
        {
            return string.Equals(card.StatusLabel, "Ready", StringComparison.OrdinalIgnoreCase)
                ? "可用"
                : card.StatusLabel;
        }

        return card.Availability switch
        {
            ToolAvailability.InDevelopment => "尚未提供",
            ToolAvailability.Paused => "已暂停",
            _ => "不可用"
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
        var cards = _products.BuildToolCards(descriptors, static _ => Task.CompletedTask, null);
        var localIds = cards
            .Select(card => card.ToolId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = new List<MobileToolEntry>(cards.Count + MobileToolCatalog.KnownToolIds.Count);
        entries.AddRange(cards.Select(MobileToolCatalog.FromLocalCard));

        var missing = MobileToolCatalog.KnownToolIds
            .Where(id => !localIds.Contains(id))
            .Select(MobileToolCatalog.BuildComputerToolDescriptor)
            .ToArray();
        if (missing.Length > 0)
        {
            var computerCards = _products.BuildToolCards(missing, static _ => Task.CompletedTask, null);
            entries.AddRange(computerCards.Select(MobileToolCatalog.FromComputerCard));
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
            .Where(card => byId.ContainsKey(card.ToolId))
            .Select(card => byId[card.ToolId])
            .Take(take)
            .ToArray();
    }
}
