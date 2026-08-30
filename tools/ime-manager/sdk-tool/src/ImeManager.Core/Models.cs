using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImeManager.MyPowerTools;

public enum InputMethodKind
{
    KeyboardLayout,
    TextService
}

public enum SwitchHotkey
{
    LeftAltShift = 1,
    CtrlShift = 2,
    NotAssigned = 3,
    GraveAccent = 4
}

public sealed record SwitchHotkeys(
    SwitchHotkey LanguageHotkey,
    SwitchHotkey LayoutHotkey)
{
    public static SwitchHotkeys WindowsDefault { get; } = new(SwitchHotkey.LeftAltShift, SwitchHotkey.CtrlShift);
}

public sealed record InputMethodInfo(
    string TipString,
    ushort LanguageId,
    string LanguageName,
    string DisplayName,
    InputMethodKind Kind,
    bool IsEnabled,
    bool IsDefault,
    Guid ProcessorClsid,
    Guid ProfileGuid,
    uint KeyboardLayoutId)
{
    public string KindLabel => Kind == InputMethodKind.TextService ? "输入法" : "键盘布局";

    public string Summary => $"{LanguageName} · {DisplayName}";
}

public sealed record InputMethodSnapshot(
    string Platform,
    IReadOnlyList<InputMethodInfo> Enabled,
    IReadOnlyList<InputMethodInfo> Available,
    string? DefaultTipString,
    SwitchHotkeys Hotkeys,
    bool WinSpaceMapsToShift = false)
{
    public static InputMethodSnapshot Unsupported { get; } = new(
        "unsupported",
        [],
        [],
        null,
        SwitchHotkeys.WindowsDefault);
}

public sealed record InputMethodPlan(
    IReadOnlyList<string> EnabledTipStrings,
    string DefaultTipString,
    SwitchHotkeys Hotkeys);

public sealed record InputMethodPlanDiff(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    bool OrderChanged,
    bool DefaultChanged,
    bool HotkeysChanged)
{
    public bool HasChanges =>
        Added.Count > 0 ||
        Removed.Count > 0 ||
        OrderChanged ||
        DefaultChanged ||
        HotkeysChanged;
}

public sealed record InputMethodApplyResult(
    InputMethodSnapshot Snapshot,
    InputMethodPlanDiff Diff);

public sealed record InputMethodReadOptions(bool IncludeAllKeyboardLayouts = false);

public static class ImeManagerJson
{
    public static JsonSerializerOptions Indented { get; } = Create(true);
    public static JsonSerializerOptions Compact { get; } = Create(false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        };
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }
}
