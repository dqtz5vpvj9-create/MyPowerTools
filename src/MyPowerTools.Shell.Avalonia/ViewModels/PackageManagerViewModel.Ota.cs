using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Platform.Windows;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed partial class PackageManagerViewModel
{
    /// <summary>
    /// Where the updater leaves its last result. SpecialFolder.LocalApplicationData is
    /// %LOCALAPPDATA% on Windows and ~/Library/Application Support on macOS, so the hint names
    /// the directory the reader will actually find rather than a Windows environment variable.
    /// </summary>
    internal static string OtaStateFileHint => Path.Combine(
        OperatingSystem.IsWindows()
            ? "%LOCALAPPDATA%"
            : Path.Combine("~", "Library", "Application Support"),
        "MyPowerTools",
        "ota-state",
        "last-update.json");

    private Task RequestUpdateConsentAsync()
    {
        if (_applyUpdate is null || IsUpdateBusy)
        {
            return Task.CompletedTask;
        }

        var consent = _createConsent?.Invoke() ?? OtaApplyConsent.Create(HasDevOverlay, includeCurrentShell: true);
        _pendingConsent = consent;
        UpdateConsentTitle = consent.Title;
        UpdateConsentIntro = consent.Intro;
        UpdateConsentCloseItems = ToConsentItems(consent.Targets.Select(target => target.DisplayName));
        UpdateConsentFootnote = consent.Footnote;
        UpdateConsentConfirmText = consent.ConfirmButtonText;
        IsUpdateConsentVisible = true;
        UpdateStatus = consent.HasTargets
            ? "请确认将关闭的程序。同意后更新器会关闭它们，并在完成后重新打开。"
            : "没有需要关闭的程序，确认后开始更新。";
        return Task.CompletedTask;
    }

    private Task CancelUpdateConsentAsync()
    {
        if (IsUpdateBusy)
        {
            return Task.CompletedTask;
        }

        _pendingConsent = null;
        IsUpdateConsentVisible = false;
        UpdateStatus = UpdateAvailable
            ? $"发现新版本 {LatestVersion}。点击“立即升级”开始更新。"
            : "尚未检查更新。";
        return Task.CompletedTask;
    }

    private async Task RunOtaCheckAsync(Func<Task<string?>>? checkUpdate)
    {
        if (checkUpdate is null || IsUpdateBusy)
        {
            return;
        }

        IsUpdateConsentVisible = false;
        IsUpdateBusy = true;
        UpdateStatus = "正在检查更新…";
        try
        {
            var output = await checkUpdate();
            if (string.IsNullOrWhiteSpace(output))
            {
                UpdateStatus = "检查更新失败：更新器没有返回结果。";
                return;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(output);
            }
            catch (JsonException)
            {
                UpdateStatus = $"检查更新失败：{CollapseOutput(output)}";
                return;
            }

            if (node is null)
            {
                UpdateStatus = $"检查更新失败：{CollapseOutput(output)}";
                return;
            }

            var error = node["error"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(error))
            {
                UpdateStatus = $"检查更新失败：{error}";
                return;
            }

            var current = node["currentVersion"]?.GetValue<string>() ?? CurrentVersion;
            var latest = node["latestVersion"]?.GetValue<string>() ?? "-";
            var available = node["available"]?.GetValue<bool>() ?? false;
            var reason = node["reason"]?.GetValue<string>() ?? "";
            CurrentVersion = current;
            LatestVersion = latest;
            UpdateAvailable = available;
            UpdateStatus = available
                ? $"发现新版本 {latest}。点击“立即升级”后，更新器会列出需要关闭的程序。"
                : $"当前已是最新版本（{reason}）。";
            if (HasDevOverlay && available)
            {
                UpdateStatus += " 开发覆盖不会自动加回，升级后如需 Debug 请再运行开发版启动脚本。";
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    private async Task RunOtaApplyAsync(Func<Action<OtaDownloadProgress>?, Task<string?>>? applyUpdate)
    {
        if (applyUpdate is null || IsUpdateBusy)
        {
            return;
        }

        PersistReopenPlan();
        IsUpdateConsentVisible = false;
        IsUpdateBusy = true;
        UpdateProgressPercent = 0;
        UpdateProgressText = "准备下载…";
        IsUpdateProgressVisible = true;
        UpdateStatus = "正在下载并升级，占用文件的程序即将关闭并在完成后重新打开…";
        try
        {
            var output = await applyUpdate(OnOtaDownloadProgress);
            if (string.IsNullOrWhiteSpace(output))
            {
                UpdateStatus = "升级失败：更新器没有返回结果。";
                return;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(output);
            }
            catch (JsonException)
            {
                UpdateStatus = $"升级失败：{CollapseOutput(output)}";
                return;
            }

            if (node is null)
            {
                UpdateStatus = $"升级失败：{CollapseOutput(output)}";
                return;
            }

            var error = node["error"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(error))
            {
                UpdateStatus = $"升级失败：{error}";
                return;
            }

            var success = node["success"]?.GetValue<bool>() ?? false;
            var toVersion = node["toVersion"]?.GetValue<string>();
            var healthOk = node["health"]?["ok"]?.GetValue<bool>() ?? false;
            if (success && !string.IsNullOrWhiteSpace(toVersion))
            {
                CurrentVersion = toVersion;
                LatestVersion = toVersion;
                UpdateAvailable = false;
                UpdateStatus = healthOk
                    ? $"已升级到 {toVersion}，健康检查通过。"
                    : $"已升级到 {toVersion}，但健康检查未完全通过，请查看 OTA 日志。";
            }
            else
            {
                UpdateStatus = $"升级失败，请查看 {OtaStateFileHint} 与日志。";
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = $"升级失败：{ex.Message}";
        }
        finally
        {
            IsUpdateProgressVisible = false;
            IsUpdateBusy = false;
        }
    }

    private void OnOtaDownloadProgress(OtaDownloadProgress progress)
    {
        UpdateProgressPercent = progress.PercentValue;
        UpdateProgressText = progress.Text;
        UpdateStatus = $"正在下载 {progress.File}：{progress.Text}…";
    }

    private static string CollapseOutput(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length <= 400)
        {
            return trimmed.Replace("\r", " ").Replace("\n", " ");
        }

        return trimmed[..400].Replace("\r", " ").Replace("\n", " ") + "…";
    }

    private void PersistReopenPlan()
    {
        if (_pendingConsent is null)
        {
            return;
        }

        var stateRoot = _otaStateRoot;
        if (string.IsNullOrWhiteSpace(stateRoot))
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            stateRoot = OtaCloseTargetScanner.DefaultStateRoot();
        }

        OtaCloseTargetScanner.WriteReopenPlan(stateRoot, _pendingConsent.Targets);
    }

    private static IReadOnlyList<OtaConsentItemViewModel> ToConsentItems(IEnumerable<string> items)
    {
        return items.Select(static item => new OtaConsentItemViewModel(item)).ToArray();
    }
}
