using MyPowerTools.Shell.Avalonia.Services.Mobile;

namespace MyPowerTools.Android.Updates;

internal sealed class AndroidModuleUpdaterAdapter : IMobileModuleUpdater
{
    private readonly AndroidModuleHotUpdaterCore _core;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _isBusy;
    private string _lastDetail = "优先 Tailscale 内网直连，不可达时自动切换公网代理";

    public AndroidModuleUpdaterAdapter(AndroidModuleHotUpdaterCore core)
    {
        _core = core;
    }

    public MobileModuleUpdateSourceConfig GetConfig()
    {
        var cfg = _core.LoadConfig();
        return new MobileModuleUpdateSourceConfig(cfg.TailscaleBaseUrl, cfg.PublicProxyBaseUrl, cfg.DirectFallbackBaseUrl);
    }

    public void SaveConfig(MobileModuleUpdateSourceConfig config)
    {
        _core.SaveConfig(new FeedConfig(config.TailscaleBaseUrl, config.PublicProxyBaseUrl, config.DirectFallbackBaseUrl));
    }

    public MobileModuleUpdateState GetState()
    {
        var overlays = _core.ListInstalledOverlays();
        var summary = _isBusy
            ? "正在检查或下载模块…"
            : overlays.Count == 0
                ? "当前使用内置模块 · Tailscale 优先"
                : $"已热更新 {overlays.Count} 个模块（最新源：{overlays[^1].SourceLabel}）";
        var list = overlays.Select(m => $"{m.Id} ({m.Version}) · {m.SourceLabel}").ToArray();
        return new MobileModuleUpdateState(summary, _lastDetail, overlays.Count > 0, _isBusy, list);
    }

    public async Task<MobileModuleUpdateResult> CheckAndApplyAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new MobileModuleUpdateResult(false, "", "", Array.Empty<string>(), 0, "已有更新任务正在进行中。");
        }

        _isBusy = true;
        try
        {
            var outcome = await _core.CheckAndApplyAsync(cancellationToken).ConfigureAwait(false);
            _lastDetail = outcome.Message;
            return new MobileModuleUpdateResult(
                outcome.Succeeded,
                outcome.SourceLabel,
                outcome.SourceUrl,
                outcome.UpdatedModules,
                outcome.DownloadedBytes,
                outcome.Message);
        }
        catch (Exception ex)
        {
            _lastDetail = ex.Message;
            AndroidStartupLog.Error("module-hotupdate", ex);
            return new MobileModuleUpdateResult(false, "", "", Array.Empty<string>(), 0, ex.Message);
        }
        finally
        {
            _isBusy = false;
            _gate.Release();
        }
    }

    public async Task<MobileModuleUpdateResult> ResetToBundledAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _isBusy = true;
        try
        {
            var outcome = await _core.ResetToBundledAsync(cancellationToken).ConfigureAwait(false);
            _lastDetail = outcome.Message;
            return new MobileModuleUpdateResult(
                outcome.Succeeded,
                outcome.SourceLabel,
                outcome.SourceUrl,
                outcome.UpdatedModules,
                outcome.DownloadedBytes,
                outcome.Message);
        }
        finally
        {
            _isBusy = false;
            _gate.Release();
        }
    }
}
