using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MyPowerTools.Platform.Mac;

/// <summary>
/// Accessibility (AXIsProcessTrusted) probe shared by the hotkey registry and the shortcut
/// sender. Synthesizing key events is refused by macOS until the host process is trusted, so the
/// hint travels in the service status text rather than turning into a silent no-op.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacAccessibility
{
    private const string ApplicationServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    internal const string PermissionHint =
        "需要在 系统设置 › 隐私与安全性 › 辅助功能 中授权 MyPowerTools。";

    /// <summary>
    /// Whether this process may post synthetic keyboard events. Never prompts: the check runs on
    /// every send and a prompt per send would be worse than the status text.
    /// </summary>
    internal static bool IsTrusted()
    {
        return AXIsProcessTrusted();
    }

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrusted();
}
