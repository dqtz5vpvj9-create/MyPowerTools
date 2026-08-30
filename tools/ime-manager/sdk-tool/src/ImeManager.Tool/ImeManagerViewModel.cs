using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImeManager.MyPowerTools;
using MyPowerTools.AvaloniaSdk;

namespace ImeManager.Tool;

public sealed class ImeManagerViewModel : MptObservableViewModel, IDisposable
{
    private const string ManagedTipsFileName = "managed-input-methods.json";

    public static IReadOnlyList<HotkeyChoice> HotkeyChoices { get; } =
    [
        new(SwitchHotkey.LeftAltShift, "左 Alt + Shift"),
        new(SwitchHotkey.CtrlShift, "Ctrl + Shift"),
        new(SwitchHotkey.GraveAccent, "波浪号键 ` ~"),
        new(SwitchHotkey.NotAssigned, "未分配")
    ];

    private readonly MptAvaloniaSurfaceContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, InputMethodInfo> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _managedTipStrings = [];
    private InputMethodPlan _savedPlan = new([], "", SwitchHotkeys.WindowsDefault);
    private bool _savedWinSpaceMapsToShift;
    private HashSet<string> _catalogSet = new(StringComparer.OrdinalIgnoreCase);
    private bool _isBusy;
    private bool _isDirty;
    private bool _isAddPanelOpen;
    private bool _includeAllKeyboardLayouts;
    private bool _suppressHotkeyMutation;
    private string _statusText = "正在读取当前用户的输入法列表…";
    private string _filterText = "";
    private string _actionMessage = "更改只作用于当前 Windows 用户，不会安装搜狗或其他输入法软件。";
    private string _defaultTipString = "";
    private InputMethodRow? _selectedItem;
    private InputMethodRow? _selectedAvailable;
    private HotkeyChoice _languageHotkey = HotkeyChoices[0];
    private HotkeyChoice _layoutHotkey = HotkeyChoices[1];
    private bool _winSpaceMapsToShift;

    public ImeManagerViewModel(MptAvaloniaSurfaceContext context)
    {
        _context = context;
        Items = [];
        AvailableItems = [];
        RefreshCommand = Command(RefreshAsync, "refresh");
        AddCommand = Command(AddAsync, "add");
        ToggleAddPanelCommand = Command(ToggleAddPanelAsync, "toggle-add");
        MoveUpCommand = Command(() => MoveAsync(-1), "move-up");
        MoveDownCommand = Command(() => MoveAsync(1), "move-down");
        SetDefaultCommand = Command(SetDefaultAsync, "set-default");
        SelectAllCommand = Command(() => SetAllCheckedAsync(true), "select-all");
        InvertSelectionCommand = Command(InvertSelectionAsync, "invert");
        ApplyCommand = Command(ApplyAsync, "apply");
        DiscardCommand = Command(DiscardAsync, "discard");
    }

    public ObservableCollection<InputMethodRow> Items { get; }
    public ObservableCollection<InputMethodRow> AvailableItems { get; }
    public IReadOnlyList<HotkeyChoice> HotkeyOptions => HotkeyChoices;

    public MptAsyncRelayCommand RefreshCommand { get; }
    public MptAsyncRelayCommand AddCommand { get; }
    public MptAsyncRelayCommand ToggleAddPanelCommand { get; }
    public MptAsyncRelayCommand MoveUpCommand { get; }
    public MptAsyncRelayCommand MoveDownCommand { get; }
    public MptAsyncRelayCommand SetDefaultCommand { get; }
    public MptAsyncRelayCommand SelectAllCommand { get; }
    public MptAsyncRelayCommand InvertSelectionCommand { get; }
    public MptAsyncRelayCommand ApplyCommand { get; }
    public MptAsyncRelayCommand DiscardCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandState();
            }
        }
    }

    public bool CanInteract => !IsBusy;
    public bool CanApply => !IsBusy && IsDirty;

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(CanApply));
                NotifyCommandState();
            }
        }
    }

    public bool IsAddPanelOpen
    {
        get => _isAddPanelOpen;
        private set => SetProperty(ref _isAddPanelOpen, value);
    }

    public bool IncludeAllKeyboardLayouts
    {
        get => _includeAllKeyboardLayouts;
        set
        {
            if (SetProperty(ref _includeAllKeyboardLayouts, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ActionMessage
    {
        get => _actionMessage;
        private set => SetProperty(ref _actionMessage, value);
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                RebuildAvailable();
            }
        }
    }

    public InputMethodRow? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public InputMethodRow? SelectedAvailable
    {
        get => _selectedAvailable;
        set => SetProperty(ref _selectedAvailable, value);
    }

    public HotkeyChoice LanguageHotkey
    {
        get => _languageHotkey;
        set
        {
            if (value is null || !SetProperty(ref _languageHotkey, value) || _suppressHotkeyMutation)
            {
                return;
            }

            MarkDirty();
        }
    }

    public HotkeyChoice LayoutHotkey
    {
        get => _layoutHotkey;
        set
        {
            if (value is null || !SetProperty(ref _layoutHotkey, value) || _suppressHotkeyMutation)
            {
                return;
            }

            MarkDirty();
        }
    }

    public bool WinSpaceMapsToShift
    {
        get => _winSpaceMapsToShift;
        set
        {
            if (SetProperty(ref _winSpaceMapsToShift, value) && !_suppressHotkeyMutation)
            {
                MarkDirty();
            }
        }
    }

    public async Task InitializeAsync()
    {
        await LoadManagedTipsAsync();
        await RefreshAsync();
    }

    public void Dispose() => _lifetime.Cancel();

    private MptAsyncRelayCommand Command(Func<Task> execute, string name)
    {
        return new MptAsyncRelayCommand(execute, () => CanInteract, $"ime-manager.{name}");
    }

    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在读取当前用户已启用和可添加的输入法…";
        try
        {
            var snapshot = await ReadSnapshotAsync();
            if (!string.Equals(snapshot.Platform, "windows", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "当前系统不是 Windows，输入法管理器不可用。";
                ActionMessage = "Windows 的输入法列表、默认项和切换顺序都是按用户保存的，其它系统没有对应接口。";
                return;
            }

            await ApplySnapshotAsync(snapshot);
            var enabledCount = Items.Count(item => item.IsChecked);
            StatusText = $"已读取 {Items.Count} 个可管理输入法，其中 {enabledCount} 个已启用。";
            ActionMessage = "勾选表示出现在 Win+空格 / Ctrl+Shift 切换列表里；取消勾选后点应用即移除。添加不会下载任何输入法软件。";
            Log("info", $"Loaded IME snapshot; managed={Items.Count}; enabled={enabledCount}.");
        }
        catch (Exception exception)
        {
            StatusText = $"读取失败：{exception.Message}";
            ActionMessage = "隔离 Runtime 未能读取当前用户的输入法列表。";
            Log("error", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task ToggleAddPanelAsync()
    {
        IsAddPanelOpen = !IsAddPanelOpen;
        return Task.CompletedTask;
    }

    private async Task AddAsync()
    {
        if (SelectedAvailable is null)
        {
            IsAddPanelOpen = true;
            return;
        }

        var tip = SelectedAvailable.TipString;
        if (Items.Any(item => string.Equals(item.TipString, tip, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var row = ToRow(tip, isChecked: true, isDefault: Items.Count == 0);
        row.CheckedChanged += OnRowCheckedChanged;
        Items.Add(row);
        AddManagedTip(tip);
        if (Items.Count == 1)
        {
            _defaultTipString = tip;
        }

        RebuildAvailable();
        SelectedItem = row;
        MarkDirty();
        IsAddPanelOpen = false;
        await SaveManagedTipsAsync();
    }

    private async Task MoveAsync(int offset)
    {
        if (SelectedItem is null)
        {
            return;
        }

        var index = Items.IndexOf(SelectedItem);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Items.Count)
        {
            return;
        }

        Items.Move(index, target);
        _managedTipStrings.Clear();
        _managedTipStrings.AddRange(Items.Select(item => item.TipString));
        MarkDirty();
        await SaveManagedTipsAsync();
    }

    private Task SetDefaultAsync()
    {
        if (SelectedItem is null)
        {
            return Task.CompletedTask;
        }

        if (!SelectedItem.IsChecked)
        {
            SelectedItem.IsChecked = true;
        }

        _defaultTipString = SelectedItem.TipString;
        RefreshDefaultBadges();
        MarkDirty();
        return Task.CompletedTask;
    }

    private Task SetAllCheckedAsync(bool isChecked)
    {
        foreach (var item in Items)
        {
            item.IsChecked = isChecked;
        }

        EnsureDefaultStillChecked();
        MarkDirty();
        return Task.CompletedTask;
    }

    private Task InvertSelectionAsync()
    {
        foreach (var item in Items)
        {
            item.IsChecked = !item.IsChecked;
        }

        EnsureDefaultStillChecked();
        MarkDirty();
        return Task.CompletedTask;
    }

    private Task DiscardAsync()
    {
        _suppressHotkeyMutation = true;
        WinSpaceMapsToShift = _savedWinSpaceMapsToShift;
        _suppressHotkeyMutation = false;
        RebuildItemsFromPlan(_savedPlan);
        StatusText = "已放弃未应用的更改。";
        return Task.CompletedTask;
    }

    private async Task ApplyAsync()
    {
        if (!CanApply)
        {
            return;
        }

        InputMethodPlan plan;
        try
        {
            plan = BuildDraftPlan();
        }
        catch (Exception exception)
        {
            ActionMessage = exception.Message;
            return;
        }

        IsBusy = true;
        StatusText = "正在写入当前用户的输入法列表、顺序和默认项…";
        try
        {
            var payload = await ExecutePayloadAsync(
                "ime-manager.apply",
                new JsonObject
                {
                    ["enabledTipStrings"] = new JsonArray(
                        plan.EnabledTipStrings.Select(tip => JsonValue.Create(tip)).ToArray()),
                    ["defaultTipString"] = plan.DefaultTipString,
                    ["languageHotkey"] = JsonSerializer.SerializeToNode(
                        plan.Hotkeys.LanguageHotkey,
                        ImeManagerJson.Compact),
                    ["layoutHotkey"] = JsonSerializer.SerializeToNode(
                        plan.Hotkeys.LayoutHotkey,
                        ImeManagerJson.Compact),
                    ["winSpaceMapsToShift"] = WinSpaceMapsToShift,
                    ["includeAllKeyboardLayouts"] = IncludeAllKeyboardLayouts
                });
            var result = payload?.Deserialize<InputMethodApplyResult>(ImeManagerJson.Compact) ??
                         throw new InvalidDataException("Runtime 未返回应用结果。");
            await ApplySnapshotAsync(result.Snapshot);
            StatusText =
                $"已应用：新增 {result.Diff.Added.Count}，移除 {result.Diff.Removed.Count}，" +
                $"默认{(result.Diff.DefaultChanged ? "已更新" : "未变")}。";
            ActionMessage = "新窗口会使用更新后的默认输入法；已打开的窗口可能仍要再按一次 Win+空格。";
            Log("info", StatusText);
        }
        catch (Exception exception)
        {
            StatusText = $"应用失败：{exception.Message}";
            ActionMessage = "当前用户的输入法列表没有按这次草稿改写；可刷新后重试。";
            Log("warning", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<InputMethodSnapshot> ReadSnapshotAsync()
    {
        var payload = await ExecutePayloadAsync(
            "ime-manager.snapshot",
            new JsonObject
            {
                ["includeAllKeyboardLayouts"] = IncludeAllKeyboardLayouts
            });
        var snapshotNode = payload?["snapshot"] ??
                           throw new InvalidDataException("Runtime 未返回输入法快照。");
        return snapshotNode.Deserialize<InputMethodSnapshot>(ImeManagerJson.Compact) ??
               throw new InvalidDataException("输入法快照无法解析。");
    }

    private async Task ApplySnapshotAsync(InputMethodSnapshot snapshot)
    {
        _catalog.Clear();
        foreach (var item in snapshot.Enabled.Concat(snapshot.Available))
        {
            _catalog[item.TipString] = item;
        }

        _catalogSet = InputMethodPlanner.CatalogSet(snapshot);
        _savedPlan = InputMethodPlanner.FromSnapshot(snapshot);
        _suppressHotkeyMutation = true;
        WinSpaceMapsToShift = snapshot.WinSpaceMapsToShift;
        _suppressHotkeyMutation = false;
        _savedWinSpaceMapsToShift = snapshot.WinSpaceMapsToShift;
        var managed = MergeManagedTipStrings(
            _managedTipStrings,
            Items.Select(item => item.TipString),
            snapshot.Enabled.Select(item => item.TipString),
            _catalogSet);
        _managedTipStrings.Clear();
        _managedTipStrings.AddRange(managed);
        RebuildItemsFromPlan(_savedPlan);
        IsAddPanelOpen = false;
        await SaveManagedTipsAsync();
    }

    private void RebuildItemsFromPlan(InputMethodPlan plan)
    {
        foreach (var item in Items)
        {
            item.CheckedChanged -= OnRowCheckedChanged;
        }

        Items.Clear();
        _defaultTipString = plan.DefaultTipString;
        var enabled = plan.EnabledTipStrings.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tip in _managedTipStrings)
        {
            var row = ToRow(
                tip,
                isChecked: enabled.Contains(tip),
                isDefault: string.Equals(tip, plan.DefaultTipString, StringComparison.OrdinalIgnoreCase));
            row.CheckedChanged += OnRowCheckedChanged;
            Items.Add(row);
        }

        _suppressHotkeyMutation = true;
        LanguageHotkey = HotkeyChoices.FirstOrDefault(item => item.Value == plan.Hotkeys.LanguageHotkey) ??
                         HotkeyChoices[0];
        LayoutHotkey = HotkeyChoices.FirstOrDefault(item => item.Value == plan.Hotkeys.LayoutHotkey) ??
                       HotkeyChoices[1];
        _suppressHotkeyMutation = false;

        RebuildAvailable();
        SelectedItem = Items.FirstOrDefault(item => item.IsDefault) ?? Items.FirstOrDefault();
        IsDirty = false;
        StatusText =
            $"当前管理 {Items.Count} 项，切换列表有 {enabled.Count} 项，默认 {DisplayName(_defaultTipString)}。";
    }

    internal static IReadOnlyList<string> MergeManagedTipStrings(
        IEnumerable<string> persistedTips,
        IEnumerable<string> visibleTips,
        IEnumerable<string> enabledTips,
        IReadOnlySet<string> catalog)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tip in persistedTips.Concat(visibleTips).Concat(enabledTips))
        {
            if (!ParsedTipString.TryParse(tip, out var parsed) ||
                !catalog.Contains(parsed.Canonical) ||
                !seen.Add(parsed.Canonical))
            {
                continue;
            }

            merged.Add(parsed.Canonical);
        }

        return merged;
    }

    private void AddManagedTip(string tipString)
    {
        var canonical = ParsedTipString.RequireCanonical(tipString);
        if (!_managedTipStrings.Contains(canonical, StringComparer.OrdinalIgnoreCase))
        {
            _managedTipStrings.Add(canonical);
        }
    }

    private async Task LoadManagedTipsAsync()
    {
        var path = Path.Combine(_context.DataDirectory, ManagedTipsFileName);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path, _lifetime.Token))?.AsObject();
            if (document?["tipStrings"] is not JsonArray tips)
            {
                return;
            }

            foreach (var node in tips)
            {
                if (node is JsonValue value &&
                    value.TryGetValue<string>(out var tip) &&
                    ParsedTipString.TryParse(tip, out var parsed) &&
                    !_managedTipStrings.Contains(parsed.Canonical, StringComparer.OrdinalIgnoreCase))
                {
                    _managedTipStrings.Add(parsed.Canonical);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Log("warning", $"读取输入法管理范围失败：{exception.Message}");
        }
    }

    private async Task SaveManagedTipsAsync()
    {
        try
        {
            Directory.CreateDirectory(_context.DataDirectory);
            var path = Path.Combine(_context.DataDirectory, ManagedTipsFileName);
            var temporaryPath = path + ".tmp";
            var document = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["tipStrings"] = new JsonArray(
                    _managedTipStrings.Select(tip => JsonValue.Create(tip)).ToArray())
            };
            await File.WriteAllTextAsync(
                temporaryPath,
                document.ToJsonString(ImeManagerJson.Compact),
                Encoding.UTF8,
                _lifetime.Token);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log("warning", $"保存输入法管理范围失败：{exception.Message}");
        }
    }

    private InputMethodPlan BuildDraftPlan()
    {
        var enabled = Items
            .Where(item => item.IsChecked)
            .Select(item => item.TipString)
            .ToArray();
        var defaultTip = enabled.Contains(_defaultTipString, StringComparer.OrdinalIgnoreCase)
            ? _defaultTipString
            : enabled.FirstOrDefault() ?? "";
        var plan = new InputMethodPlan(
            enabled,
            defaultTip,
            new SwitchHotkeys(LanguageHotkey.Value, LayoutHotkey.Value));
        InputMethodPlanner.Validate(plan, _catalogSet);
        return plan;
    }

    private void RebuildAvailable()
    {
        var listed = Items.Select(item => item.TipString).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filter = FilterText.Trim();
        var selected = SelectedAvailable?.TipString;
        AvailableItems.Clear();
        foreach (var item in _catalog.Values
                     .Where(item => !listed.Contains(item.TipString))
                     .Where(item => MatchesFilter(item, filter))
                     .OrderBy(item => item.LanguageName, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            AvailableItems.Add(ToRow(item.TipString, isChecked: false, isDefault: false));
        }

        SelectedAvailable = AvailableItems.FirstOrDefault(
            item => string.Equals(item.TipString, selected, StringComparison.OrdinalIgnoreCase));
    }

    private InputMethodRow ToRow(string tipString, bool isChecked, bool isDefault)
    {
        var info = _catalog.TryGetValue(tipString, out var known)
            ? known
            : new InputMethodInfo(
                tipString,
                0,
                "未知语言",
                tipString,
                InputMethodKind.TextService,
                isChecked,
                isDefault,
                Guid.Empty,
                Guid.Empty,
                0);
        return new InputMethodRow(
            info.TipString,
            info.Summary,
            info.KindLabel,
            isChecked,
            isDefault);
    }

    private void OnRowCheckedChanged(object? sender, EventArgs e)
    {
        EnsureDefaultStillChecked();
        MarkDirty();
    }

    private void EnsureDefaultStillChecked()
    {
        if (Items.Any(item => item.IsChecked &&
                              string.Equals(item.TipString, _defaultTipString, StringComparison.OrdinalIgnoreCase)))
        {
            RefreshDefaultBadges();
            return;
        }

        var firstChecked = Items.FirstOrDefault(item => item.IsChecked);
        _defaultTipString = firstChecked?.TipString ?? _defaultTipString;
        RefreshDefaultBadges();
    }

    private void RefreshDefaultBadges()
    {
        foreach (var item in Items)
        {
            item.IsDefault = string.Equals(
                item.TipString,
                _defaultTipString,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private void MarkDirty()
    {
        try
        {
            var draft = BuildDraftPlan();
            IsDirty = InputMethodPlanner.Diff(_savedPlan, draft).HasChanges ||
                      _winSpaceMapsToShift != _savedWinSpaceMapsToShift;
            var checkedCount = Items.Count(item => item.IsChecked);
            StatusText = IsDirty
                ? $"未应用更改：切换列表 {checkedCount} 项，默认 {DisplayName(_defaultTipString)}。"
                : $"当前切换列表有 {checkedCount} 项，默认 {DisplayName(_defaultTipString)}。";
        }
        catch (Exception exception)
        {
            IsDirty = true;
            ActionMessage = exception.Message;
        }

        NotifyCommandState();
    }

    private string DisplayName(string tipString) =>
        _catalog.TryGetValue(tipString, out var info) ? info.DisplayName : tipString;

    private static bool MatchesFilter(InputMethodInfo item, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return item.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
               item.LanguageName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
               item.TipString.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<JsonObject?> ExecutePayloadAsync(
        string commandId,
        JsonObject? arguments = null)
    {
        var command = await _context.ExecuteCommandAsync(
            commandId,
            arguments,
            _lifetime.Token);
        if (!command.Success)
        {
            throw new InvalidOperationException(
                command.Error?.Message ??
                (string.IsNullOrWhiteSpace(command.Output)
                    ? "MPT Runtime 命令执行失败。"
                    : command.Output));
        }

        var response = JsonNode.Parse(command.Output)?.AsObject() ??
                       throw new InvalidDataException("Runtime 输出不是 JSON-RPC 对象。");
        var result = response["result"]?.AsObject() ??
                     throw new InvalidDataException("Runtime 输出缺少 result。");
        var state = result["state"]?.GetValue<string>() ?? "failed";
        if (!string.Equals(state, "ready", StringComparison.OrdinalIgnoreCase))
        {
            var message = result["error"]?["message"]?.GetValue<string>() ??
                          "Runtime 拒绝了该命令。";
            throw new InvalidOperationException(message);
        }

        return result["payload"] as JsonObject;
    }

    private void Log(string level, string message)
    {
        _context.Log(new MptSurfaceLogEntry(level, message, DateTimeOffset.UtcNow));
    }

    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(CanApply));
        RefreshCommand.NotifyCanExecuteChanged();
        AddCommand.NotifyCanExecuteChanged();
        ToggleAddPanelCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        SetDefaultCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }
}

public sealed record HotkeyChoice(SwitchHotkey Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class InputMethodRow : MptObservableViewModel
{
    private bool _isChecked;
    private bool _isDefault;

    public InputMethodRow(
        string tipString,
        string summary,
        string kindLabel,
        bool isChecked,
        bool isDefault)
    {
        TipString = tipString;
        Summary = summary;
        KindLabel = kindLabel;
        _isChecked = isChecked;
        _isDefault = isDefault;
    }

    public event EventHandler? CheckedChanged;

    public string TipString { get; }
    public string Summary { get; }
    public string KindLabel { get; }

    public override string ToString() => Summary;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsDefault
    {
        get => _isDefault;
        set => SetProperty(ref _isDefault, value);
    }
}
