using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Android;

/// <summary>
/// The <see cref="IPlatformPack"/> surface Android does not implement. The MPT capability interfaces
/// stay available (host and module code keeps working unchanged), but every provider is described with
/// Android wording instead of leaking the Linux/desktop fallback text into the mobile UI.
/// </summary>
public static class AndroidUnsupportedServices
{
    public const string Provider = "Android";

    public static IDisplayService Display { get; } = new UnsupportedDisplayService(Provider, "手机端暂不支持配置外接显示器。");

    public static ITrayService Tray { get; } = new UnsupportedTrayService(Provider, "手机端使用系统状态栏与通知，不提供桌面托盘。");

    public static IKeyboardShortcutService KeyboardShortcuts { get; } = new UnsupportedKeyboardShortcutService(Provider, "手机端不提供向其它应用注入键盘快捷方式的能力。");

    public static IAutostartService Autostart { get; } = new UnsupportedAutostartService(Provider, "Android 由系统管理应用启动，不提供桌面自启动项。");

    public static IServiceManager Services { get; } = new UnsupportedServiceManager(Provider, "Android 不提供桌面服务单元，请使用前台后台任务。");

    public static INetworkBroker Network { get; } = new UnsupportedNetworkBroker(Provider, "手机端不提供系统端口转发规则。");

    public static IHotkeyService Hotkeys { get; } = new UnsupportedHotkeyService(Provider, "手机端不提供全局热键。");

    public static IPrivilegeBroker Privileges { get; } = new UnsupportedPrivilegeBroker(Provider, "Android 使用系统运行权限授权，不提供桌面提权代理。");

    public static IProcessService Processes { get; } = new UnsupportedProcessService(Provider, "受 Android 应用沙箱限制，无法枚举系统进程。");
}
