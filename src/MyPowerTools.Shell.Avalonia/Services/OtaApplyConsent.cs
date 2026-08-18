using MyPowerTools.Platform.Windows;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed record OtaApplyConsent(
    string Title,
    string Intro,
    IReadOnlyList<OtaCloseTarget> Targets,
    string Footnote)
{
    public bool HasTargets => Targets.Count > 0;

    public string ConfirmButtonText => HasTargets ? "关闭并开始升级" : "开始升级";

    public static OtaApplyConsent Create(
        bool hasDevOverlay,
        IReadOnlyList<OtaCloseTarget>? targets = null,
        bool includeCurrentShell = false)
    {
        var detected = (targets ?? DetectTargets()).ToList();
        if (includeCurrentShell && detected.All(target => target.Id != "shell"))
        {
            detected.Insert(0, new OtaCloseTarget("shell", "MyPowerTools"));
        }

        detected.Sort(static (left, right) =>
            string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase));

        var intro = detected.Count == 0
            ? "没有检测到正在使用安装文件的程序，可以直接开始更新。"
            : "以下程序正在使用需要更新的文件。更新器将关闭它们，并在完成后重新打开。";
        var footnote = hasDevOverlay
            ? "当前是开发覆盖，升级后不会自动加回本地 Debug。"
            : "";

        return new OtaApplyConsent(
            detected.Count == 0 ? "可以开始更新" : "需要关闭以下正在运行的程序",
            intro,
            detected,
            footnote);
    }

    private static IReadOnlyList<OtaCloseTarget> DetectTargets()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return OtaCloseTargetScanner.Scan();
    }
}
