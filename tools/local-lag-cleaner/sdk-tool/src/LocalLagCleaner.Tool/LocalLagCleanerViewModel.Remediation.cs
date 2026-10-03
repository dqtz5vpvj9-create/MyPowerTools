using System.Collections.ObjectModel;
using System.Text.Json;
using LocalLagCleaner.MyPowerTools;
using MyPowerTools.AvaloniaSdk;

namespace LocalLagCleaner.Tool;

public sealed partial class LocalLagCleanerViewModel
{
    private int _selectedTabIndex;
    private RemediationRow? _selectedRemediation;
    private bool _isCareRunning;
    public bool CanCancelScan => IsBusy && !_isCareRunning;
    private string _careSummary = "点击自动处理，工具会连续检查、处理支持的项目，再复测效果。";
    private MptAsyncRelayCommand? _automaticCareCommand;
    private MptAsyncRelayCommand? _restoreCareCommand;
    public MptAsyncRelayCommand AutomaticCareCommand => _automaticCareCommand ??= Command(() => RunAutomaticCareAsync("all"), "automatic-care");
    public MptAsyncRelayCommand RestoreCareCommand => _restoreCareCommand ??= Command(RestoreCareAsync, "restore-care");
    public ObservableCollection<CareItem> CareResults { get; } = [];
    public string CareSummary { get => _careSummary; private set => SetProperty(ref _careSummary, value); }
    public ObservableCollection<RemediationRow> Remediations { get; } = [];
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }
    public RemediationRow? SelectedRemediation
    {
        get => _selectedRemediation;
        set => SetProperty(ref _selectedRemediation, value);
    }

    private RemediationRow CreateRemediationRow(LagDiagnosticSnapshot snapshot, LagFinding finding)
    {
        var plan = RemediationCatalog.Create(snapshot, finding);
        return new RemediationRow(plan, Command(() => ExecuteRemediationAsync(plan.Entry), "remediation"));
    }

    private Task ExecuteRemediationAsync(RemediationEntry entry) => entry switch
    {
        RemediationEntry.DeepScan => ScanAsync(deep: true),
        RemediationEntry.ElevatedFileScan => ScanElevatedFileHandlesAsync(),
        RemediationEntry.McpCleanup => PlanMcpCommand.ExecuteAsync(),
        RemediationEntry.WeFlowCleanup => PlanWeFlowCommand.ExecuteAsync(),
        RemediationEntry.NvidiaRestart => PlanNvidiaCommand.ExecuteAsync(),
        RemediationEntry.AutomaticMemory => RunAutomaticCareAsync("memory"),
        RemediationEntry.AutomaticKernel => RunAutomaticCareAsync("kernel"),
        RemediationEntry.AutomaticStorage => RunAutomaticCareAsync("storage"),
        RemediationEntry.AutomaticBackground => RunAutomaticCareAsync("background"),
        RemediationEntry.AutomaticAll => RunAutomaticCareAsync("all"),
        _ => OpenDiagnosticToolAsync(entry)
    };

    private async Task RunAutomaticCareAsync(string scope)
    {
        if (IsBusy) return;
        _isCareRunning = true;
        IsBusy = true;
        SelectedTabIndex = 0;
        CareResults.Clear();
        CareSummary = "正在自动检查、处理并复测，通常需要约 30 秒…";
        StatusText = CareSummary;
        try
        {
            var payload = await ExecutePayloadAsync($"local-lag-cleaner.care.{scope}");
            var result = payload?.Deserialize<CareResult>(LagCleanerJson.Compact) ?? throw new InvalidDataException("自动处理结果缺失。");
            foreach (var item in result.Items) CareResults.Add(item);
            _snapshot = result.After;
            ApplySnapshot(result.After, deep: true);
            CareSummary = result.Summary;
            ActionMessage = result.Summary;
            StatusText = result.Summary;
        }
        catch (Exception exception)
        {
            CareSummary = $"处理未完成：{exception.Message}";
            StatusText = CareSummary;
        }
        finally { _isCareRunning = false; IsBusy = false; }
    }

    private async Task LoadLastCareAsync()
    {
        try
        {
            var payload = await ExecutePayloadAsync("local-lag-cleaner.care.last");
            if (payload?["summary"] is null) return;
            var result = payload.Deserialize<CareResult>(LagCleanerJson.Compact);
            if (result is null) return;
            CareResults.Clear();
            foreach (var item in result.Items) CareResults.Add(item);
            CareSummary = $"上次处理（{result.After.CapturedAtUtc.ToLocalTime():MM-dd HH:mm}）：{result.Summary}";
        }
        catch (Exception exception) { Log("warning", $"读取上次处理结果失败：{exception.Message}"); }
    }

    private async Task RestoreCareAsync()
    {
        if (IsBusy) return;
        _isCareRunning = true;
        IsBusy = true;
        try
        {
            // The response payload is an object for consistency with all surface commands.
            var payload = await ExecutePayloadAsync("local-lag-cleaner.care.restore");
            var results = payload?["items"]?.Deserialize<CareItem[]>(LagCleanerJson.Compact) ?? [];
            CareResults.Clear();
            foreach (var item in results) CareResults.Add(item);
            CareSummary = results.Length == 0 ? "当前没有需要撤销的后台调整。" : "后台调整恢复检查已完成。";
        }
        catch (Exception exception) { CareSummary = $"恢复未完成：{exception.Message}"; }
        finally { _isCareRunning = false; IsBusy = false; }
    }

    private async Task OpenDiagnosticToolAsync(RemediationEntry entry)
    {
        var suffix = entry switch
        {
            RemediationEntry.ResourceMonitor => "resource-monitor",
            RemediationEntry.TaskManager => "task-manager",
            RemediationEntry.StorageSettings => "storage",
            RemediationEntry.StartupSettings => "startup",
            RemediationEntry.WindowsUpdate => "update",
            RemediationEntry.DeviceManager => "devices",
            RemediationEntry.ReliabilityMonitor => "reliability",
            _ => throw new ArgumentOutOfRangeException(nameof(entry))
        };
        IsBusy = true;
        try
        {
            var payload = await ExecutePayloadAsync($"local-lag-cleaner.open.{suffix}");
            ActionMessage = payload?["message"]?.GetValue<string>() ?? "已请求打开诊断工具。";
        }
        catch (Exception exception)
        {
            ActionMessage = $"打开诊断工具失败：{exception.Message}";
            Log("warning", ActionMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed record RemediationRow(FindingRemediation Plan, MptAsyncRelayCommand PrimaryCommand);
