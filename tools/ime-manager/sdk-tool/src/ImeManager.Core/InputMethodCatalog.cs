namespace ImeManager.MyPowerTools;

public interface IInputMethodPlatform
{
    bool IsSupported { get; }

    InputMethodSnapshot ReadSnapshot(InputMethodReadOptions options);

    void Enable(string tipString);

    void Disable(string tipString);

    void WriteEnabledOrder(IReadOnlyList<string> enabledTipStrings);

    void SetDefault(string tipString);

    void SetHotkeys(SwitchHotkeys hotkeys);

    void NotifyChanged();
}

public sealed class InputMethodCatalog
{
    private readonly IInputMethodPlatform _platform;

    public InputMethodCatalog(IInputMethodPlatform platform)
    {
        _platform = platform;
    }

    public InputMethodSnapshot Read(InputMethodReadOptions? options = null)
    {
        if (!_platform.IsSupported)
        {
            return InputMethodSnapshot.Unsupported;
        }

        return _platform.ReadSnapshot(options ?? new InputMethodReadOptions());
    }

    public InputMethodApplyResult Apply(
        InputMethodPlan plan,
        InputMethodReadOptions? options = null)
    {
        if (!_platform.IsSupported)
        {
            throw new InvalidOperationException("输入法管理器仅支持 Windows。");
        }

        var readOptions = options ?? new InputMethodReadOptions();
        var currentSnapshot = _platform.ReadSnapshot(readOptions);
        var catalog = InputMethodPlanner.CatalogSet(currentSnapshot);
        foreach (var tip in plan.EnabledTipStrings)
        {
            catalog.Add(ParsedTipString.RequireCanonical(tip));
        }

        InputMethodPlanner.Validate(plan, catalog);
        var currentPlan = InputMethodPlanner.FromSnapshot(currentSnapshot);
        var diff = InputMethodPlanner.Diff(currentPlan, plan);
        if (!diff.HasChanges)
        {
            return new InputMethodApplyResult(currentSnapshot, diff);
        }

        try
        {
            foreach (var tip in diff.Added)
            {
                _platform.Enable(tip);
            }

            foreach (var tip in diff.Removed)
            {
                _platform.Disable(tip);
            }

            if (diff.Added.Count > 0 ||
                diff.Removed.Count > 0 ||
                diff.OrderChanged)
            {
                _platform.WriteEnabledOrder(plan.EnabledTipStrings);
            }

            if (diff.DefaultChanged || diff.Added.Count > 0 || diff.Removed.Count > 0)
            {
                _platform.SetDefault(plan.DefaultTipString);
            }

            if (diff.HotkeysChanged)
            {
                _platform.SetHotkeys(plan.Hotkeys);
            }

            _platform.NotifyChanged();
        }
        catch (Exception exception) when (diff.HasChanges)
        {
            try
            {
                Restore(currentPlan);
            }
            catch (Exception restoreException)
            {
                throw new InvalidOperationException(
                    $"应用输入法设置失败：{exception.Message}；恢复原列表也失败：{restoreException.Message}",
                    exception);
            }

            throw new InvalidOperationException(
                $"应用输入法设置失败，已尝试恢复原列表：{exception.Message}",
                exception);
        }

        return new InputMethodApplyResult(_platform.ReadSnapshot(readOptions), diff);
    }

    private void Restore(InputMethodPlan original)
    {
        foreach (var tip in original.EnabledTipStrings)
        {
            _platform.Enable(tip);
        }

        _platform.WriteEnabledOrder(original.EnabledTipStrings);
        _platform.SetDefault(original.DefaultTipString);
        _platform.SetHotkeys(original.Hotkeys);
        _platform.NotifyChanged();
    }
}
