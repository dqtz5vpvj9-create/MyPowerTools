namespace ImeManager.MyPowerTools;

public static class InputMethodPlanner
{
    public static InputMethodPlan FromSnapshot(InputMethodSnapshot snapshot)
    {
        var enabled = snapshot.Enabled
            .Select(item => ParsedTipString.RequireCanonical(item.TipString))
            .ToArray();
        var defaultTip = string.IsNullOrWhiteSpace(snapshot.DefaultTipString)
            ? enabled.FirstOrDefault() ?? ""
            : ParsedTipString.RequireCanonical(snapshot.DefaultTipString);
        if (enabled.Length > 0 &&
            !enabled.Contains(defaultTip, StringComparer.OrdinalIgnoreCase))
        {
            defaultTip = enabled[0];
        }

        return new InputMethodPlan(enabled, defaultTip, snapshot.Hotkeys);
    }

    public static InputMethodPlan Add(
        InputMethodPlan plan,
        string tipString,
        IReadOnlySet<string> catalog)
    {
        var canonical = RequireKnown(tipString, catalog);
        if (plan.EnabledTipStrings.Contains(canonical, StringComparer.OrdinalIgnoreCase))
        {
            return plan;
        }

        var enabled = plan.EnabledTipStrings.ToList();
        enabled.Add(canonical);
        var defaultTip = string.IsNullOrWhiteSpace(plan.DefaultTipString)
            ? canonical
            : plan.DefaultTipString;
        var next = new InputMethodPlan(enabled, defaultTip, plan.Hotkeys);
        Validate(next, catalog);
        return next;
    }

    public static InputMethodPlan Remove(
        InputMethodPlan plan,
        string tipString,
        IReadOnlySet<string> catalog)
    {
        var canonical = ParsedTipString.RequireCanonical(tipString);
        var enabled = plan.EnabledTipStrings
            .Where(item => !string.Equals(item, canonical, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (enabled.Count == plan.EnabledTipStrings.Count)
        {
            return plan;
        }

        var defaultTip = plan.DefaultTipString;
        if (enabled.Count == 0)
        {
            throw new InvalidOperationException("至少保留一种输入法，避免系统无法输入。");
        }

        if (!enabled.Contains(defaultTip, StringComparer.OrdinalIgnoreCase))
        {
            defaultTip = enabled[0];
        }

        var next = new InputMethodPlan(enabled, defaultTip, plan.Hotkeys);
        Validate(next, catalog);
        return next;
    }

    public static InputMethodPlan Move(
        InputMethodPlan plan,
        string tipString,
        int offset,
        IReadOnlySet<string> catalog)
    {
        if (offset == 0)
        {
            return plan;
        }

        var canonical = ParsedTipString.RequireCanonical(tipString);
        var enabled = plan.EnabledTipStrings.ToList();
        var index = enabled.FindIndex(
            item => string.Equals(item, canonical, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new InvalidOperationException("只能调整已启用输入法的顺序。");
        }

        var target = index + offset;
        if (target < 0 || target >= enabled.Count)
        {
            return plan;
        }

        enabled.RemoveAt(index);
        enabled.Insert(target, canonical);
        var next = new InputMethodPlan(enabled, plan.DefaultTipString, plan.Hotkeys);
        Validate(next, catalog);
        return next;
    }

    public static InputMethodPlan SetDefault(
        InputMethodPlan plan,
        string tipString,
        IReadOnlySet<string> catalog)
    {
        var canonical = RequireKnown(tipString, catalog);
        if (!plan.EnabledTipStrings.Contains(canonical, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只能把已启用的输入法设为默认。");
        }

        var next = new InputMethodPlan(plan.EnabledTipStrings, canonical, plan.Hotkeys);
        Validate(next, catalog);
        return next;
    }

    public static InputMethodPlan SetHotkeys(
        InputMethodPlan plan,
        SwitchHotkeys hotkeys,
        IReadOnlySet<string> catalog)
    {
        if (!Enum.IsDefined(hotkeys.LanguageHotkey) ||
            !Enum.IsDefined(hotkeys.LayoutHotkey))
        {
            throw new ArgumentException("切换快捷键取值无效。", nameof(hotkeys));
        }

        var next = new InputMethodPlan(plan.EnabledTipStrings, plan.DefaultTipString, hotkeys);
        Validate(next, catalog);
        return next;
    }

    public static void Validate(InputMethodPlan plan, IReadOnlySet<string> catalog)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(catalog);
        if (plan.EnabledTipStrings.Count == 0)
        {
            throw new InvalidOperationException("至少保留一种输入法，避免系统无法输入。");
        }

        if (plan.EnabledTipStrings.Count > ParsedTipString.MaximumEnabledCount)
        {
            throw new InvalidOperationException(
                $"最多启用 {ParsedTipString.MaximumEnabledCount} 种输入法。");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tip in plan.EnabledTipStrings)
        {
            var canonical = RequireKnown(tip, catalog);
            if (!seen.Add(canonical))
            {
                throw new InvalidOperationException($"输入法列表包含重复项：{canonical}");
            }
        }

        var defaultTip = RequireKnown(plan.DefaultTipString, catalog);
        if (!seen.Contains(defaultTip))
        {
            throw new InvalidOperationException("默认输入法必须位于已启用列表中。");
        }

        if (!Enum.IsDefined(plan.Hotkeys.LanguageHotkey) ||
            !Enum.IsDefined(plan.Hotkeys.LayoutHotkey))
        {
            throw new InvalidOperationException("切换快捷键取值无效。");
        }
    }

    public static InputMethodPlanDiff Diff(InputMethodPlan current, InputMethodPlan desired)
    {
        var currentSet = current.EnabledTipStrings.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredSet = desired.EnabledTipStrings.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = desired.EnabledTipStrings
            .Where(item => !currentSet.Contains(item))
            .Select(ParsedTipString.RequireCanonical)
            .ToArray();
        var removed = current.EnabledTipStrings
            .Where(item => !desiredSet.Contains(item))
            .Select(ParsedTipString.RequireCanonical)
            .ToArray();
        var orderChanged = added.Length == 0 &&
            removed.Length == 0 &&
            !current.EnabledTipStrings.SequenceEqual(
                desired.EnabledTipStrings,
                StringComparer.OrdinalIgnoreCase);
        var defaultChanged = !string.Equals(
            current.DefaultTipString,
            desired.DefaultTipString,
            StringComparison.OrdinalIgnoreCase);
        var hotkeysChanged = current.Hotkeys != desired.Hotkeys;
        return new InputMethodPlanDiff(added, removed, orderChanged, defaultChanged, hotkeysChanged);
    }

    public static HashSet<string> CatalogSet(InputMethodSnapshot snapshot)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshot.Enabled.Concat(snapshot.Available))
        {
            if (ParsedTipString.TryParse(item.TipString, out var parsed))
            {
                set.Add(parsed.Canonical);
            }
        }

        return set;
    }

    private static string RequireKnown(string tipString, IReadOnlySet<string> catalog)
    {
        var canonical = ParsedTipString.RequireCanonical(tipString);
        if (!catalog.Contains(canonical))
        {
            throw new InvalidOperationException($"系统未安装该输入法：{canonical}");
        }

        return canonical;
    }
}
