namespace MyPowerTools.MobileToolControl;

/// <summary>What a command does to the computer, as far as the phone can honestly tell.</summary>
internal enum MobileToolIntent
{
    /// <summary>A read that the page can present as a status/statistics action.</summary>
    Read,

    /// <summary>Anything that is not a known read; the page makes no claim beyond the catalog's own text.</summary>
    Action
}

/// <summary>Presentation of one real command for the three priority tools.</summary>
internal sealed record MobileToolSemantic(string CommandId, string Action, string Hint, MobileToolIntent Intent);

/// <summary>
/// Understandable wording for the computer tools the approved prototype calls out first: Input Monitor,
/// ScreenEase and Paste Image. Every entry is keyed by the real HostControl command id those modules
/// publish (<c>tools/input-monitor</c>, <c>tools/screenease</c>, <c>tools/paste-image</c> manifests), so
/// the page can only decorate a command the computer actually offered - an unknown command falls back
/// to the catalog's own title and subtitle and is never given an invented label.
/// </summary>
internal static class MobileToolControlSemantics
{
    private static readonly Dictionary<string, MobileToolSemantic> Known = new(StringComparer.Ordinal)
    {
        // ---- Input Monitor (tools/input-monitor/current-integration/modules/input-monitor/commands.index.json)
        ["input-monitor.stats"] = new("input-monitor.stats", "查看使用统计", "今天的键鼠与应用活动", MobileToolIntent.Read),
        ["input-monitor.snapshot"] = new("input-monitor.snapshot", "立即取一次快照", "读取当前输入活动", MobileToolIntent.Read),
        ["input-monitor.rest"] = new("input-monitor.rest", "现在休息一下", "在电脑上开始一次休息提醒", MobileToolIntent.Action),
        ["input-monitor.pause"] = new("input-monitor.pause", "暂停提醒", "暂时停止休息提醒", MobileToolIntent.Action),
        ["input-monitor.skip"] = new("input-monitor.skip", "跳过这次提醒", "跳过当前休息提醒", MobileToolIntent.Action),
        ["input-monitor.set-category"] = new("input-monitor.set-category", "设置分类", "标记这次活动属于哪类工作", MobileToolIntent.Action),
        ["input-monitor.data.clear"] = new("input-monitor.data.clear", "清除本机记录", "删除电脑上保存的输入统计", MobileToolIntent.Action),

        // ---- ScreenEase (tools/screenease/current-integration/modules/screenease/commands.index.json)
        ["screenease.effect.status"] = new("screenease.effect.status", "查看当前显示方案", "亮度、色温与滤镜状态", MobileToolIntent.Read),
        ["screenease.overlay.status"] = new("screenease.overlay.status", "查看遮罩状态", "当前叠加层是否生效", MobileToolIntent.Read),
        ["screenease.native-writer.status"] = new("screenease.native-writer.status", "查看硬件写入状态", "原生亮度/色温写入是否可用", MobileToolIntent.Read),
        ["screenease.reminder.status"] = new("screenease.reminder.status", "查看护眼提醒", "提醒是否开启与下次时间", MobileToolIntent.Read),
        ["screenease.settings.read"] = new("screenease.settings.read", "读取显示设置", "电脑上保存的显示偏好", MobileToolIntent.Read),
        ["screenease.effect.toggle"] = new("screenease.effect.toggle", "开启或关闭护眼", "切换当前显示方案", MobileToolIntent.Action),
        ["screenease.effect.disable"] = new("screenease.effect.disable", "关闭护眼", "恢复到未处理显示", MobileToolIntent.Action),
        ["screenease.effect.brightness.increase"] = new("screenease.effect.brightness.increase", "提高亮度", "把屏幕亮度调高", MobileToolIntent.Action),
        ["screenease.effect.brightness.decrease"] = new("screenease.effect.brightness.decrease", "降低亮度", "把屏幕亮度调低", MobileToolIntent.Action),
        ["screenease.effect.temperature.increase"] = new("screenease.effect.temperature.increase", "调暖色温", "让屏幕更暖", MobileToolIntent.Action),
        ["screenease.effect.temperature.decrease"] = new("screenease.effect.temperature.decrease", "调冷色温", "让屏幕更冷", MobileToolIntent.Action),
        ["screenease.effect.apply"] = new("screenease.effect.apply", "应用显示方案", "按参数设置亮度与色温", MobileToolIntent.Action),
        ["screenease.profile.apply-low-blue-evening"] = new("screenease.profile.apply-low-blue-evening", "晚间低蓝光", "切换到晚间护眼方案", MobileToolIntent.Action),
        ["screenease.profile.apply-long-read"] = new("screenease.profile.apply-long-read", "长读柔光", "切换到长时间阅读方案", MobileToolIntent.Action),
        ["screenease.profile.open"] = new("screenease.profile.open", "打开方案页", "在电脑上打开显示方案", MobileToolIntent.Action),
        ["screenease.overlay.toggle"] = new("screenease.overlay.toggle", "切换遮罩", "打开或关闭显示遮罩", MobileToolIntent.Action),
        ["screenease.overlay.configure"] = new("screenease.overlay.configure", "设置遮罩", "调整遮罩参数", MobileToolIntent.Action),
        ["screenease.reminder.start"] = new("screenease.reminder.start", "开始护眼提醒", "按设定间隔提醒休息", MobileToolIntent.Action),
        ["screenease.reminder.pause"] = new("screenease.reminder.pause", "暂停护眼提醒", "暂时不提醒", MobileToolIntent.Action),
        ["screenease.reminder.resume"] = new("screenease.reminder.resume", "继续护眼提醒", "恢复提醒", MobileToolIntent.Action),
        ["screenease.reminder.reset"] = new("screenease.reminder.reset", "重置护眼提醒", "重新开始计时", MobileToolIntent.Action),
        ["screenease.schedule.configure"] = new("screenease.schedule.configure", "设置日程", "按时间自动切换方案", MobileToolIntent.Action),
        ["screenease.legacy.import"] = new("screenease.legacy.import", "导入旧设置", "从旧版本迁移显示设置", MobileToolIntent.Action),

        // ---- Paste Image (tools/paste-image/current-integration/modules/paste-image/commands.index.json)
        ["paste-image.history"] = new("paste-image.history", "最近的图片", "电脑上传过的图片与路径", MobileToolIntent.Read),
        ["paste-image.inspect"] = new("paste-image.inspect", "查看一张图片", "读取图片信息与远端路径", MobileToolIntent.Read),
        ["paste-image.clipboard.probe"] = new("paste-image.clipboard.probe", "查看剪贴板状态", "电脑剪贴板里现在有什么", MobileToolIntent.Read),
        ["paste-image.upload"] = new("paste-image.upload", "上传图片", "把手机图片送到电脑剪贴板", MobileToolIntent.Action),
        ["paste-image.notification.test"] = new("paste-image.notification.test", "发送一条测试通知", "验证电脑端通知是否可达", MobileToolIntent.Action)
    };

    /// <summary>The tools whose actions the page presents first, in prototype order.</summary>
    private static readonly string[] PriorityTools = ["input-monitor", "screenease", "paste-image"];

    private static readonly Dictionary<string, string> ToolTitles = new(StringComparer.Ordinal)
    {
        ["input-monitor"] = "输入监测",
        ["screenease"] = "屏幕舒适",
        ["paste-image"] = "图片快传"
    };

    private static readonly Dictionary<string, string> ToolPurposes = new(StringComparer.Ordinal)
    {
        ["input-monitor"] = "查看使用习惯，记得适时休息",
        ["screenease"] = "让屏幕适合此刻的光线",
        ["paste-image"] = "上传剪贴板图片，接着粘贴"
    };

    public static bool IsPriorityTool(string toolId) =>
        PriorityTools.Contains(toolId, StringComparer.Ordinal);

    public static IReadOnlyList<string> PriorityToolIds => PriorityTools;

    /// <summary>Friendly title for a known tool id, or the catalog's own title.</summary>
    public static string ToolTitle(string toolId, string catalogTitle) =>
        ToolTitles.TryGetValue(toolId, out var title) ? title : catalogTitle;

    public static string ToolPurpose(string toolId, string catalogDescription) =>
        ToolPurposes.TryGetValue(toolId, out var purpose) ? purpose : catalogDescription;

    /// <summary>The semantic entry for a command, or <see langword="null"/> when only the catalog knows it.</summary>
    public static MobileToolSemantic? Find(string commandId) =>
        Known.TryGetValue(commandId, out var semantic) ? semantic : null;

    /// <summary>
    /// Catalog-first presentation of one command. A known command gets the understandable action
    /// wording; an unknown command keeps exactly the title and subtitle the computer sent, and its
    /// intent is inferred from the catalog's own "只读" wording only.
    /// </summary>
    public static (string Action, string Hint, MobileToolIntent Intent) Describe(MobileToolCommandItem command)
    {
        var semantic = Find(command.CommandId);
        if (semantic is not null)
        {
            var hint = command.Subtitle.Length > 0 ? command.Subtitle : semantic.Hint;
            return (semantic.Action, hint, semantic.Intent);
        }

        var title = command.Title.Length > 0 ? command.Title : command.CommandId;
        var subtitle = command.Subtitle;
        var read = subtitle.Contains("只读", StringComparison.Ordinal) ||
                   command.SafetyLabel == "只读";
        return (title, subtitle, read ? MobileToolIntent.Read : MobileToolIntent.Action);
    }

    /// <summary>Splits the real catalog into priority-tool actions and everything else.</summary>
    public static (IReadOnlyList<MobileToolCommandItem> Priority, IReadOnlyList<MobileToolCommandItem> Others) Split(
        IReadOnlyList<MobileToolCommandItem> commands)
    {
        var priority = new List<MobileToolCommandItem>();
        var others = new List<MobileToolCommandItem>();
        foreach (var command in commands)
        {
            if (Find(command.CommandId) is not null)
            {
                priority.Add(command);
            }
            else
            {
                others.Add(command);
            }
        }

        return (priority, others);
    }

    /// <summary>Groups the remaining commands by the module that owns them.</summary>
    public static IReadOnlyList<(string ModuleId, string Title, IReadOnlyList<MobileToolCommandItem> Commands)> GroupByTool(
        IReadOnlyList<MobileToolCommandItem> commands,
        IReadOnlyList<MobileToolToolItem> tools)
    {
        var order = new List<string>();
        var grouped = new Dictionary<string, List<MobileToolCommandItem>>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            var moduleId = command.ModuleId.Length > 0 ? command.ModuleId : command.CommandId.Split('.')[0];
            if (!grouped.TryGetValue(moduleId, out var list))
            {
                list = [];
                grouped[moduleId] = list;
                order.Add(moduleId);
            }

            list.Add(command);
        }

        return order
            .Select(moduleId => (
                moduleId,
                Title: tools.FirstOrDefault(tool => string.Equals(tool.ToolId, moduleId, StringComparison.Ordinal))?.Title
                       ?? ToolTitle(moduleId, moduleId),
                Commands: (IReadOnlyList<MobileToolCommandItem>)grouped[moduleId]))
            .ToArray();
    }
}
