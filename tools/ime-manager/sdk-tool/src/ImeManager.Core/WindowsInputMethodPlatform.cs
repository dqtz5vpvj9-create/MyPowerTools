using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace ImeManager.MyPowerTools;

[SupportedOSPlatform("windows")]
public sealed class WindowsInputMethodPlatform : IInputMethodPlatform
{
    private const uint IlotDisabled = 0x00000080;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private const uint SpiSetLangToggle = 0x005B;
    private const uint SpiUpdateIniFile = 0x0001;
    private const uint SpiSendChange = 0x0002;
    private const int MaxPathChars = 260;
    private static readonly Guid KeyboardCategory = new("337B5AB2-EFBB-4C8A-9D35-62104D90C267");
    private static readonly Guid SpeechCategory = new("337B5AB4-EFBB-4C8A-9D35-62104D90C267");
    private static readonly Guid HandwritingCategory = new("337B5AB5-EFBB-4C8A-9D35-62104D90C267");
    private static readonly HashSet<uint> CommonKeyboardLayouts =
    [
        0x00000409,
        0x00000804,
        0x00000404,
        0x00000411,
        0x00000412,
        0x00000407,
        0x0000040C,
        0x00000419,
        0x00000809,
        0x0000040A,
        0x00000410
    ];

    public bool IsSupported => OperatingSystem.IsWindows();

    public InputMethodSnapshot ReadSnapshot(InputMethodReadOptions options)
    {
        EnsureWindows();
        var catalog = ReadInstalledCatalog();
        var enabledOrder = ReadEnabledOrder();
        var defaultTip = ReadDefaultTip(enabledOrder);
        var enabledLanguageIds = enabledOrder
            .Select(tip => ParsedTipString.TryParse(tip, out var parsed) ? parsed.LanguageId : (ushort)0)
            .Where(languageId => languageId != 0)
            .ToHashSet();
        var enabled = new List<InputMethodInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tip in enabledOrder)
        {
            if (!seen.Add(tip))
            {
                continue;
            }

            enabled.Add(ResolveInfo(tip, catalog, isEnabled: true, isDefault: false));
        }

        if (enabled.Count > 0 &&
            (string.IsNullOrWhiteSpace(defaultTip) ||
             !seen.Contains(defaultTip)))
        {
            defaultTip = enabled[0].TipString;
        }

        enabled = enabled
            .Select(item => item with
            {
                IsDefault = string.Equals(item.TipString, defaultTip, StringComparison.OrdinalIgnoreCase)
            })
            .ToList();

        var available = catalog.Values
            .Where(item => !seen.Contains(item.TipString))
            .Where(item => ShouldOffer(item, enabledLanguageIds, options.IncludeAllKeyboardLayouts))
            .OrderBy(item => item.LanguageName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => item with { IsEnabled = false, IsDefault = false })
            .ToList();

        return new InputMethodSnapshot(
            "windows",
            enabled,
            available,
            defaultTip,
            ReadHotkeys());
    }

    public void Enable(string tipString)
    {
        EnsureWindows();
        InvokeInstall(ParsedTipString.RequireCanonical(tipString), dwFlags: 0);
    }

    public void Disable(string tipString)
    {
        EnsureWindows();
        InvokeInstall(ParsedTipString.RequireCanonical(tipString), IlotDisabled);
    }

    public void WriteEnabledOrder(IReadOnlyList<string> enabledTipStrings)
    {
        EnsureWindows();
        var parsed = enabledTipStrings.Select(ParsedTipString.RequireCanonical)
            .Select(tip =>
            {
                if (!ParsedTipString.TryParse(tip, out var value))
                {
                    throw new InvalidOperationException($"不是有效的输入法标识：{tip}");
                }

                return value;
            })
            .ToArray();
        WriteSortOrder(parsed);
        WriteUserProfile(parsed);
        WritePreload(parsed);
    }

    public void SetDefault(string tipString)
    {
        EnsureWindows();
        var canonical = ParsedTipString.RequireCanonical(tipString);
        InvokeSetDefault(canonical);
        using var profile = Registry.CurrentUser.CreateSubKey(
            @"Control Panel\International\User Profile",
            writable: true);
        profile.SetValue("InputMethodOverride", canonical, RegistryValueKind.String);
    }

    public void SetHotkeys(SwitchHotkeys hotkeys)
    {
        EnsureWindows();
        if (!Enum.IsDefined(hotkeys.LanguageHotkey) ||
            !Enum.IsDefined(hotkeys.LayoutHotkey))
        {
            throw new InvalidOperationException("切换快捷键取值无效。");
        }

        using var key = Registry.CurrentUser.CreateSubKey(@"Keyboard Layout\Toggle", writable: true);
        key.SetValue("Language Hotkey", ((int)hotkeys.LanguageHotkey).ToString(CultureInfo.InvariantCulture));
        key.SetValue("Layout Hotkey", ((int)hotkeys.LayoutHotkey).ToString(CultureInfo.InvariantCulture));
        key.SetValue("Hotkey", ((int)hotkeys.LanguageHotkey).ToString(CultureInfo.InvariantCulture));
    }

    public void NotifyChanged()
    {
        EnsureWindows();
        _ = NativeMethods.SystemParametersInfo(
            SpiSetLangToggle,
            0,
            IntPtr.Zero,
            SpiUpdateIniFile | SpiSendChange);
        NativeMethods.SendNotifyMessage(
            NativeMethods.HwndBroadcast,
            NativeMethods.WmSettingChange,
            IntPtr.Zero,
            "intl");
    }

    private static bool IsNonKeyboardService(string displayName) =>
        displayName.Contains("语音识别", StringComparison.OrdinalIgnoreCase) ||
        displayName.Contains("触摸输入", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldOffer(
        InputMethodInfo item,
        HashSet<ushort> enabledLanguageIds,
        bool includeAllKeyboardLayouts)
    {
        if (item.Kind == InputMethodKind.TextService)
        {
            return true;
        }

        if (includeAllKeyboardLayouts ||
            CommonKeyboardLayouts.Contains(item.KeyboardLayoutId) ||
            enabledLanguageIds.Contains(item.LanguageId))
        {
            return true;
        }

        return false;
    }

    private static Dictionary<string, InputMethodInfo> ReadInstalledCatalog()
    {
        var catalog = new Dictionary<string, InputMethodInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ReadKeyboardLayouts().Concat(ReadTextServices()))
        {
            catalog[item.TipString] = item;
        }

        return catalog;
    }

    private static IEnumerable<InputMethodInfo> ReadKeyboardLayouts()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts");
        if (key is null)
        {
            yield break;
        }

        foreach (var name in key.GetSubKeyNames())
        {
            if (!uint.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var klid))
            {
                continue;
            }

            using var layout = key.OpenSubKey(name);
            if (layout is null)
            {
                continue;
            }

            var displayName = ReadDisplayName(
                layout.GetValue("Layout Display Name") as string,
                layout.GetValue("Layout Text") as string,
                $"键盘布局 {name.ToUpperInvariant()}");
            var languageId = (ushort)(klid & 0xFFFF);
            var tip = ParsedTipString.CanonicalKeyboard(languageId, klid);
            yield return new InputMethodInfo(
                tip,
                languageId,
                LanguageName(languageId),
                displayName,
                InputMethodKind.KeyboardLayout,
                IsEnabled: false,
                IsDefault: false,
                Guid.Empty,
                Guid.Empty,
                klid);
        }
    }

    private static IEnumerable<InputMethodInfo> ReadTextServices()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var tipRoot = hklm.OpenSubKey(@"SOFTWARE\Microsoft\CTF\TIP");
            if (tipRoot is null)
            {
                continue;
            }

            foreach (var clsidName in tipRoot.GetSubKeyNames())
            {
                if (!Guid.TryParse(clsidName, out var clsid))
                {
                    continue;
                }

                using var tipKey = tipRoot.OpenSubKey(clsidName);
                if (tipKey is null)
                {
                    continue;
                }

                var category = ParseGuid(tipKey.GetValue("Category") as string);
                if (category == SpeechCategory || category == HandwritingCategory)
                {
                    continue;
                }

                if (category != Guid.Empty && category != KeyboardCategory)
                {
                    continue;
                }

                using var languageRoot = tipKey.OpenSubKey("LanguageProfile");
                if (languageRoot is null)
                {
                    continue;
                }

                foreach (var languageName in languageRoot.GetSubKeyNames())
                {
                    if (!TryParseLanguageKey(languageName, out var languageId) ||
                        languageId is 0 or 0xFFFF)
                    {
                        continue;
                    }

                    using var languageKey = languageRoot.OpenSubKey(languageName);
                    if (languageKey is null)
                    {
                        continue;
                    }

                    foreach (var profileName in languageKey.GetSubKeyNames())
                    {
                        if (!Guid.TryParse(profileName, out var profile))
                        {
                            continue;
                        }

                        using var profileKey = languageKey.OpenSubKey(profileName);
                        var description = ReadDisplayName(
                            profileKey?.GetValue("Display Description") as string,
                            profileKey?.GetValue("Description") as string,
                            $"文字服务 {clsid.ToString("B").ToUpperInvariant()}");
                        if (IsNonKeyboardService(description))
                        {
                            continue;
                        }
                        var tip = ParsedTipString.CanonicalTextService(languageId, clsid, profile);
                        yield return new InputMethodInfo(
                            tip,
                            languageId,
                            LanguageName(languageId),
                            description,
                            InputMethodKind.TextService,
                            IsEnabled: false,
                            IsDefault: false,
                            clsid,
                            profile,
                            0);
                    }
                }
            }
        }
    }

    private static List<string> ReadEnabledOrder()
    {
        var authoritative = ReadUserProfileTips().Concat(ReadSortOrderTips());
        return MergeEnabledOrder(authoritative, ReadPreloadTips());
    }

    internal static List<string> MergeEnabledOrder(
        IEnumerable<string> authoritativeTips,
        IEnumerable<string> legacyPreloadTips)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var authoritativeLanguages = new HashSet<ushort>();
        foreach (var tip in authoritativeTips)
        {
            if (!ParsedTipString.TryParse(tip, out var parsed))
            {
                continue;
            }

            authoritativeLanguages.Add(parsed.LanguageId);
            if (seen.Add(parsed.Canonical))
            {
                ordered.Add(parsed.Canonical);
            }
        }

        foreach (var tip in legacyPreloadTips)
        {
            if (!ParsedTipString.TryParse(tip, out var parsed) ||
                authoritativeLanguages.Contains(parsed.LanguageId))
            {
                continue;
            }

            if (seen.Add(parsed.Canonical))
            {
                ordered.Add(parsed.Canonical);
            }
        }

        return ordered;
    }

    private static IEnumerable<string> ReadUserProfileTips()
    {
        using var profile = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile");
        if (profile is null)
        {
            yield break;
        }

        var languages = profile.GetValue("Languages") as string[] ?? [];
        foreach (var languageTag in languages)
        {
            using var languageKey = profile.OpenSubKey(languageTag);
            if (languageKey is null)
            {
                continue;
            }

            var languageTips = new List<string>();
            foreach (var valueName in languageKey.GetValueNames())
            {
                if (ParsedTipString.TryParse(valueName, out var parsed))
                {
                    languageTips.Add(parsed.Canonical);
                }
            }

            foreach (var tip in OrderBySortOrder(languageTips))
            {
                yield return tip;
            }
        }
    }

    private static IEnumerable<string> ReadSortOrderTips()
    {
        using var languageRoot = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\CTF\SortOrder\Language");
        if (languageRoot is null)
        {
            yield break;
        }

        foreach (var indexName in languageRoot.GetValueNames().OrderBy(name => name, StringComparer.Ordinal))
        {
            var languageValue = languageRoot.GetValue(indexName) as string;
            if (!TryParseLanguageKey(languageValue, out var languageId))
            {
                continue;
            }

            using var assembly = Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\CTF\SortOrder\AssemblyItem\0x{languageId:X8}");
            if (assembly is null)
            {
                continue;
            }

            foreach (var itemName in assembly.GetValueNames().OrderBy(name => name, StringComparer.Ordinal))
            {
                var raw = assembly.GetValue(itemName) as string;
                if (ParsedTipString.TryParseAssemblyItem(languageId, raw, out var parsed))
                {
                    yield return parsed.Canonical;
                }
            }
        }
    }

    private static IEnumerable<string> ReadPreloadTips()
    {
        using var preload = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload");
        if (preload is null)
        {
            yield break;
        }

        foreach (var name in preload.GetValueNames().OrderBy(value => value, StringComparer.Ordinal))
        {
            var raw = Convert.ToString(preload.GetValue(name), CultureInfo.InvariantCulture);
            if (TryParsePreloadValue(raw, out var canonical))
            {
                yield return canonical;
            }
        }
    }

    private static IEnumerable<string> OrderBySortOrder(IReadOnlyList<string> tips)
    {
        if (tips.Count <= 1)
        {
            return tips;
        }

        var languageId = ParsedTipString.TryParse(tips[0], out var first) ? first.LanguageId : (ushort)0;
        var sort = ReadSortOrderTips()
            .Where(tip => ParsedTipString.TryParse(tip, out var parsed) && parsed.LanguageId == languageId)
            .ToList();
        if (sort.Count == 0)
        {
            return tips;
        }

        var remaining = tips.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var tip in sort)
        {
            if (remaining.Remove(tip))
            {
                ordered.Add(tip);
            }
        }

        ordered.AddRange(tips.Where(tip => remaining.Contains(tip)));
        return ordered;
    }

    private static string? ReadDefaultTip(IReadOnlyList<string> enabled)
    {
        using var profile = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile");
        var overrideTip = profile?.GetValue("InputMethodOverride") as string;
        if (ParsedTipString.TryParse(overrideTip, out var parsed) &&
            enabled.Contains(parsed.Canonical, StringComparer.OrdinalIgnoreCase))
        {
            return parsed.Canonical;
        }

        return enabled.FirstOrDefault();
    }

    private static SwitchHotkeys ReadHotkeys()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Toggle");
        var language = ParseHotkey(
            key?.GetValue("Language Hotkey") as string ?? key?.GetValue("Hotkey") as string,
            SwitchHotkey.LeftAltShift);
        var layout = ParseHotkey(
            key?.GetValue("Layout Hotkey") as string,
            SwitchHotkey.CtrlShift);
        return new SwitchHotkeys(language, layout);
    }

    private static SwitchHotkey ParseHotkey(string? raw, SwitchHotkey fallback)
    {
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
            Enum.IsDefined(typeof(SwitchHotkey), value))
        {
            return (SwitchHotkey)value;
        }

        return fallback;
    }

    private static InputMethodInfo ResolveInfo(
        string tipString,
        IReadOnlyDictionary<string, InputMethodInfo> catalog,
        bool isEnabled,
        bool isDefault)
    {
        if (catalog.TryGetValue(tipString, out var known))
        {
            return known with { IsEnabled = isEnabled, IsDefault = isDefault };
        }

        ParsedTipString.TryParse(tipString, out var parsed);
        return new InputMethodInfo(
            parsed.Canonical,
            parsed.LanguageId,
            LanguageName(parsed.LanguageId),
            parsed.Kind == InputMethodKind.TextService ? "未知输入法" : "未知键盘布局",
            parsed.Kind,
            isEnabled,
            isDefault,
            parsed.ProcessorClsid,
            parsed.ProfileGuid,
            parsed.KeyboardLayoutId);
    }

    private static void WriteSortOrder(IReadOnlyList<ParsedTipString> enabled)
    {
        using var languageKey = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\CTF\SortOrder\Language",
            writable: true);
        ClearValues(languageKey);
        var languages = enabled.Select(item => item.LanguageId).Distinct().ToArray();
        for (var index = 0; index < languages.Length; index++)
        {
            languageKey.SetValue(
                index.ToString("00000000", CultureInfo.InvariantCulture),
                SortOrderLanguageValue(languages[index]),
                RegistryValueKind.String);
        }

        using (var assemblyRoot = Registry.CurrentUser.CreateSubKey(
                   @"Software\Microsoft\CTF\SortOrder\AssemblyItem",
                   writable: true))
        {
            foreach (var leftover in assemblyRoot.GetSubKeyNames())
            {
                assemblyRoot.DeleteSubKeyTree(leftover, throwOnMissingSubKey: false);
            }
        }

        foreach (var languageGroup in enabled.GroupBy(item => item.LanguageId))
        {
            using var assembly = Registry.CurrentUser.CreateSubKey(
                $@"Software\Microsoft\CTF\SortOrder\AssemblyItem\0x{languageGroup.Key:X8}",
                writable: true);
            ClearValues(assembly);
            var index = 0;
            foreach (var item in languageGroup)
            {
                assembly.SetValue(
                    index.ToString("00000000", CultureInfo.InvariantCulture),
                    item.ToAssemblyItemValue(),
                    RegistryValueKind.String);
                index++;
            }
        }
    }

    private static void WriteUserProfile(IReadOnlyList<ParsedTipString> enabled)
    {
        using var profile = Registry.CurrentUser.CreateSubKey(
            @"Control Panel\International\User Profile",
            writable: true);
        var existingTags = new Dictionary<ushort, string>();
        foreach (var name in profile.GetSubKeyNames())
        {
            using var existing = profile.OpenSubKey(name);
            if (existing is null)
            {
                continue;
            }

            foreach (var valueName in existing.GetValueNames())
            {
                if (ParsedTipString.TryParse(valueName, out var parsed) &&
                    !existingTags.ContainsKey(parsed.LanguageId))
                {
                    existingTags[parsed.LanguageId] = name;
                }
            }
        }

        var languageTags = new List<string>();
        var seenLanguages = new HashSet<ushort>();
        foreach (var item in enabled)
        {
            if (!seenLanguages.Add(item.LanguageId))
            {
                continue;
            }

            var tag = existingTags.TryGetValue(item.LanguageId, out var known)
                ? known
                : LanguageTag(item.LanguageId);
            languageTags.Add(tag);
        }

        profile.SetValue("Languages", languageTags.ToArray(), RegistryValueKind.MultiString);

        foreach (var languageGroup in enabled.GroupBy(item => item.LanguageId))
        {
            var tag = existingTags.TryGetValue(languageGroup.Key, out var known)
                ? known
                : LanguageTag(languageGroup.Key);
            using var languageKey = profile.CreateSubKey(tag, writable: true);
            foreach (var valueName in languageKey.GetValueNames())
            {
                if (ParsedTipString.TryParse(valueName, out _))
                {
                    languageKey.DeleteValue(valueName, throwOnMissingValue: false);
                }
            }

            languageKey.SetValue("CachedLanguageName", LanguageName(languageGroup.Key), RegistryValueKind.String);
            foreach (var item in languageGroup)
            {
                languageKey.SetValue(item.Canonical, 1, RegistryValueKind.DWord);
            }
        }
    }

    private static void WritePreload(IReadOnlyList<ParsedTipString> enabled)
    {
        using var preload = Registry.CurrentUser.CreateSubKey(@"Keyboard Layout\Preload", writable: true);
        ClearValues(preload);
        var index = 1;
        foreach (var item in enabled)
        {
            preload.SetValue(index.ToString(CultureInfo.InvariantCulture), PreloadValue(item), RegistryValueKind.String);
            index++;
        }
    }

    /// <summary>Windows stores Preload entries as eight hexadecimal digits, so <see cref="TryParsePreloadValue"/> can read them back.</summary>
    internal static string PreloadValue(ParsedTipString item) =>
        item.Kind == InputMethodKind.KeyboardLayout
            ? item.KeyboardLayoutId.ToString("X8", CultureInfo.InvariantCulture)
            : item.LanguageId.ToString("X8", CultureInfo.InvariantCulture);

    internal static bool TryParsePreloadValue(string? raw, out string canonical)
    {
        canonical = string.Empty;
        if (raw is null ||
            !uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var klid))
        {
            return false;
        }

        canonical = ParsedTipString.CanonicalKeyboard((ushort)(klid & 0xFFFF), klid);
        return true;
    }

    private static void InvokeInstall(string tipString, uint dwFlags)
    {
        using var input = NativeMethods.LoadInputDll();
        var function = NativeMethods.GetInstallLayoutOrTip(input);
        if (!function(tipString, dwFlags))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"InstallLayoutOrTip 失败：{tipString}");
        }
    }

    private static void InvokeSetDefault(string tipString)
    {
        using var input = NativeMethods.LoadInputDll();
        var function = NativeMethods.GetSetDefaultLayoutOrTip(input);
        if (!function(tipString, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetDefaultLayoutOrTip 失败：{tipString}");
        }
    }

    private static void ClearValues(RegistryKey key)
    {
        foreach (var name in key.GetValueNames())
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    private static string ReadDisplayName(string? indirect, string? fallback, string lastResort)
    {
        if (!string.IsNullOrWhiteSpace(indirect) && TryLoadIndirectString(indirect, out var resolved))
        {
            return resolved;
        }

        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        return lastResort;
    }

    private static bool TryLoadIndirectString(string source, out string value)
    {
        var buffer = new StringBuilder(MaxPathChars);
        var result = NativeMethods.SHLoadIndirectString(source, buffer, buffer.Capacity, IntPtr.Zero);
        if (result != 0 || buffer.Length == 0)
        {
            value = "";
            return false;
        }

        value = buffer.ToString();
        return true;
    }

    private static string LanguageName(ushort languageId)
    {
        if (languageId == 0)
        {
            return "未知语言";
        }

        try
        {
            return CultureInfo.GetCultureInfo(languageId).DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return $"语言 0x{languageId:X4}";
        }
    }

    private static string LanguageTag(ushort languageId)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(languageId).Name;
            return string.IsNullOrWhiteSpace(name) ? $"und-x-{languageId:x4}" : name;
        }
        catch (CultureNotFoundException)
        {
            return $"und-x-{languageId:x4}";
        }
    }

    /// <summary>Windows stores SortOrder language ids as hexadecimal, so <see cref="TryParseLanguageKey"/> can read them back.</summary>
    internal static string SortOrderLanguageValue(ushort languageId) =>
        languageId.ToString("X8", CultureInfo.InvariantCulture);

    internal static bool TryParseLanguageKey(string? raw, out ushort languageId)
    {
        languageId = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out languageId);
    }

    private static Guid ParseGuid(string? raw) =>
        Guid.TryParse(raw, out var guid) ? guid : Guid.Empty;

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("输入法管理器仅支持 Windows。");
        }
    }

    private static class NativeMethods
    {
        public static readonly IntPtr HwndBroadcast = new(0xFFFF);
        public const uint WmSettingChange = 0x001A;

        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        public delegate bool InstallLayoutOrTip(string psz, uint dwFlags);

        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        public delegate bool SetDefaultLayoutOrTip(string psz, uint dwFlags);

        public static SafeLibraryHandle LoadInputDll()
        {
            var handle = LoadLibraryEx("input.dll", IntPtr.Zero, LoadLibrarySearchSystem32);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载 System32\\input.dll。");
            }

            return new SafeLibraryHandle(handle);
        }

        public static InstallLayoutOrTip GetInstallLayoutOrTip(SafeLibraryHandle library)
        {
            var pointer = GetProcAddress(library.DangerousGetHandle(), "InstallLayoutOrTip");
            if (pointer == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "input.dll 缺少 InstallLayoutOrTip。");
            }

            return Marshal.GetDelegateForFunctionPointer<InstallLayoutOrTip>(pointer);
        }

        public static SetDefaultLayoutOrTip GetSetDefaultLayoutOrTip(SafeLibraryHandle library)
        {
            var pointer = GetProcAddress(library.DangerousGetHandle(), "SetDefaultLayoutOrTip");
            if (pointer == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "input.dll 缺少 SetDefaultLayoutOrTip。");
            }

            return Marshal.GetDelegateForFunctionPointer<SetDefaultLayoutOrTip>(pointer);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string lpLibFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        public static extern int SHLoadIndirectString(
            string pszSource,
            StringBuilder pszOutBuf,
            int cchOutBuf,
            IntPtr ppvReserved);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SystemParametersInfo(
            uint uiAction,
            uint uiParam,
            IntPtr pvParam,
            uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool SendNotifyMessage(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            string lParam);
    }

    private sealed class SafeLibraryHandle : IDisposable
    {
        private IntPtr _handle;

        public SafeLibraryHandle(IntPtr handle)
        {
            _handle = handle;
        }

        public IntPtr DangerousGetHandle() => _handle;

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                _ = NativeMethods.FreeLibrary(handle);
            }
        }
    }
}
