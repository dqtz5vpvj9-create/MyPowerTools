using NssmManager.Contracts;

namespace NssmManager.Tool;

public enum NssmBrowseTarget { Application, Directory, Stdin, Stdout, Stderr, Hook }

public sealed partial class NssmManagerViewModel
{
    private static readonly string[] GuiHookEvents = ["Start", "Stop", "Exit", "Power", "Rotate"];
    private static readonly IReadOnlyDictionary<string, string[]> GuiHookActions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Start"] = ["Pre", "Post"], ["Stop"] = ["Pre"], ["Exit"] = ["Post"],
        ["Power"] = ["Change", "Resume"], ["Rotate"] = ["Pre", "Post"]
    };
    private bool _interactiveLogonEnabled = true;
    private bool _credentialsEnabled;
    private bool _affinityEnabled;
    private bool _rotationOptionsEnabled;
    private bool _consoleTimeoutEnabled = true;
    private bool _windowTimeoutEnabled = true;
    private bool _threadsTimeoutEnabled = true;
    private int _selectedTabIndex;
    private string _selectedHookEvent = "Start";
    private string _selectedHookAction = "Pre";
    private string _selectedHookCommand = string.Empty;
    private IReadOnlyList<string> _availableHookActions = GuiHookActions["Start"];

    public bool InteractiveLogonEnabled { get => _interactiveLogonEnabled; private set => SetProperty(ref _interactiveLogonEnabled, value); }
    public bool CredentialsEnabled { get => _credentialsEnabled; private set => SetProperty(ref _credentialsEnabled, value); }
    public bool AffinityEnabled { get => _affinityEnabled; private set => SetProperty(ref _affinityEnabled, value); }
    public bool RotationOptionsEnabled { get => _rotationOptionsEnabled; private set => SetProperty(ref _rotationOptionsEnabled, value); }
    public bool ConsoleTimeoutEnabled { get => _consoleTimeoutEnabled; private set => SetProperty(ref _consoleTimeoutEnabled, value); }
    public bool WindowTimeoutEnabled { get => _windowTimeoutEnabled; private set => SetProperty(ref _windowTimeoutEnabled, value); }
    public bool ThreadsTimeoutEnabled { get => _threadsTimeoutEnabled; private set => SetProperty(ref _threadsTimeoutEnabled, value); }
    public int SelectedTabIndex { get => _selectedTabIndex; set => SetProperty(ref _selectedTabIndex, value); }
    public IReadOnlyList<string> HookEvents => GuiHookEvents;
    public IReadOnlyList<string> AvailableHookActions { get => _availableHookActions; private set => SetProperty(ref _availableHookActions, value); }
    public string SelectedHookEvent { get => _selectedHookEvent; set { if (SetProperty(ref _selectedHookEvent, value)) set_hook_tab(Array.IndexOf(GuiHookEvents, value), 0, false); } }
    public string SelectedHookAction { get => _selectedHookAction; set { if (SetProperty(ref _selectedHookAction, value)) LoadSelectedHook(); } }
    public string SelectedHookCommand { get => _selectedHookCommand; set => SetProperty(ref _selectedHookCommand, value); }

    [NssmUpstreamFunction("src/gui.cpp", 10, "static HWND dialog(const TCHAR *templ, HWND parent, DLGPROC function, LPARAM l)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    internal async Task<int> dialog(string template, Func<Task<int>> function, NssmServiceConfiguration? service)
    {
        if (service is not null) { _loaded = service; Apply(service); }
        Status = template;
        return await function().ConfigureAwait(true);
    }

    [NssmUpstreamFunction("src/gui.cpp", 25, "static HWND dialog(const TCHAR *templ, HWND parent, DLGPROC function)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    internal Task<int> dialog(string template, Func<Task<int>> function) => dialog(template, function, null);

    [NssmUpstreamFunction("src/gui.cpp", 29, "static inline void set_logon_enabled(unsigned char interact_enabled, unsigned char credentials_enabled)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    internal void set_logon_enabled(bool interactEnabled, bool credentialsEnabled)
    {
        InteractiveLogonEnabled = interactEnabled;
        CredentialsEnabled = credentialsEnabled;
    }

    [NssmUpstreamFunction("src/gui.cpp", 36, "int nssm_gui(int resource, nssm_service_t *service)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    internal async Task<int> nssm_gui(string resource, NssmServiceConfiguration? service)
    {
        centre_window();
        return resource.ToUpperInvariant() switch
        {
            "INSTALL" => await dialog(resource, async () => { await NewAsync().ConfigureAwait(true); return 0; }, service).ConfigureAwait(true),
            "EDIT" => service is null ? 1 : await dialog(resource, () => Task.FromResult(0), service).ConfigureAwait(true),
            "REMOVE" => service is null ? 1 : await dialog(resource, async () => await remove().ConfigureAwait(true), service).ConfigureAwait(true),
            _ => 1
        };
    }

    [NssmUpstreamFunction("src/gui.cpp", 242, "void centre_window(HWND window)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    internal void centre_window() => SelectedTabIndex = 0;

    [NssmUpstreamFunction("src/gui.cpp", 265, "static inline void check_stop_method(nssm_service_t *service, unsigned long method, unsigned long control)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    public void check_stop_method(uint method, bool enabled)
    {
        StopMethodSkip = enabled ? StopMethodSkip & ~method : StopMethodSkip | method;
    }

    [NssmUpstreamFunction("src/gui.cpp", 270, "static inline void check_number(HWND tab, unsigned long control, unsigned long *timeout)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    public static void check_number(string? text, ref uint value)
    {
        if (uint.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var configured)) value = configured;
    }

    [NssmUpstreamFunction("src/gui.cpp", 276, "static inline void set_timeout_enabled(unsigned long control, unsigned long dependent)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    public void set_timeout_enabled(uint method, bool enabled)
    {
        if (method == 1) ConsoleTimeoutEnabled = enabled;
        else if (method == 2) WindowTimeoutEnabled = enabled;
        else if (method == 4) ThreadsTimeoutEnabled = enabled;
        check_stop_method(method, enabled);
    }

    [NssmUpstreamFunction("src/gui.cpp", 282, "static inline void set_affinity_enabled(unsigned char enabled)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    public void set_affinity_enabled(bool enabled)
    {
        AffinityEnabled = enabled;
        if (!enabled) Affinity = "All";
    }

    [NssmUpstreamFunction("src/gui.cpp", 286, "static inline void set_rotation_enabled(unsigned char enabled)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    public void set_rotation_enabled(bool enabled)
    {
        RotationOptionsEnabled = enabled;
        RotateFiles = enabled;
    }

    [NssmUpstreamFunction("src/gui.cpp", 292, "static inline int hook_env(const TCHAR *hook_event, const TCHAR *hook_action, TCHAR *buffer, unsigned long buflen)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    public static int hook_env(string hookEvent, string hookAction, uint bufferLength, out string buffer)
    {
        buffer = $"NSSM_HOOK_{hookEvent}_{hookAction}";
        if (bufferLength == 0 || buffer.Length >= bufferLength) { buffer = string.Empty; return -1; }
        return buffer.Length;
    }

    [NssmUpstreamFunction("src/gui.cpp", 296, "static inline void set_hook_tab(int event_index, int action_index, bool changed)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    public void set_hook_tab(int eventIndex, int actionIndex, bool changed)
    {
        if (changed) update_hook(Name, _selectedHookEvent, _selectedHookAction, SelectedHookCommand);
        if (eventIndex < 0 || eventIndex >= GuiHookEvents.Length) return;
        _selectedHookEvent = GuiHookEvents[eventIndex];
        OnPropertyChanged(nameof(SelectedHookEvent));
        AvailableHookActions = GuiHookActions[_selectedHookEvent];
        _selectedHookAction = AvailableHookActions[Math.Clamp(actionIndex, 0, AvailableHookActions.Count - 1)];
        OnPropertyChanged(nameof(SelectedHookAction));
        LoadSelectedHook();
    }

    [NssmUpstreamFunction("src/gui.cpp", 363, "static inline int update_hook(TCHAR *service_name, const TCHAR *hook_event, const TCHAR *hook_action)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    public int update_hook(string serviceName, string hookEvent, string hookAction, string command)
    {
        if (!GuiHookActions.TryGetValue(hookEvent, out var actions) || !actions.Contains(hookAction, StringComparer.OrdinalIgnoreCase)) return 1;
        var hooks = ParseHookDrafts();
        hooks.RemoveAll(hook => hook.Event.Equals(hookEvent, StringComparison.OrdinalIgnoreCase) && hook.Action.Equals(hookAction, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(command)) hooks.Add(new NssmHook(hookEvent, hookAction, command));
        HooksText = string.Join(Environment.NewLine, hooks.Select(hook => $"{hook.Event}/{hook.Action}={hook.Command}"));
        return 0;
    }

    [NssmUpstreamFunction("src/gui.cpp", 373, "static inline int update_hooks(TCHAR *service_name)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    public int update_hooks(string serviceName)
    {
        try
        {
            var hooks = ParseHooks();
            return hooks.Any(hook => !GuiHookActions.TryGetValue(hook.Event, out var actions) || !actions.Contains(hook.Action, StringComparer.OrdinalIgnoreCase)) ? 1 : 0;
        }
        catch (ArgumentException) { return 1; }
    }

    [NssmUpstreamFunction("src/gui.cpp", 386, "static inline void check_io(HWND owner, TCHAR *name, TCHAR *buffer, unsigned long len, unsigned long control)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    public static string check_io(string name, string? path, uint length)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        if (length == 0 || path.Length >= length) throw new ArgumentException($"{name} path is too long.", name);
        return path;
    }

    [NssmUpstreamFunction("src/gui.cpp", 394, "int configure(HWND window, nssm_service_t *service, nssm_service_t *orig_service)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    internal NssmServiceConfiguration configure(NssmServiceConfiguration? original)
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("服务名不能为空。", nameof(Name));
        if (string.IsNullOrWhiteSpace(Application)) throw new ArgumentException("应用路径不能为空。", nameof(Application));
        _ = check_io(nameof(Stdin), Stdin, 32767);
        _ = check_io(nameof(Stdout), Stdout, 32767);
        _ = check_io(nameof(Stderr), Stderr, 32767);
        if (!Affinity.Equals("All", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(Affinity)) throw new ArgumentException("CPU 亲和性不能为空。", nameof(Affinity));
        if (Interactive && !Account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("交互式服务只能使用 LocalSystem。", nameof(Account));
        if (update_hooks(Name) != 0) throw new ArgumentException("Hook 事件或动作无效。", nameof(HooksText));
        return Build();
    }

    [NssmUpstreamFunction("src/gui.cpp", 752, "int install(HWND window)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    internal async Task<int> install()
    {
        _ = configure(null);
        await SaveAsync().ConfigureAwait(true);
        return _isNew ? 1 : 0;
    }

    [NssmUpstreamFunction("src/gui.cpp", 802, "int remove(HWND window)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    internal async Task<int> remove()
    {
        if (_loaded is null || _isNew) return 2;
        await DeleteAsync().ConfigureAwait(true);
        return _loaded is null ? 0 : 4;
    }

    [NssmUpstreamFunction("src/gui.cpp", 849, "int edit(HWND window, nssm_service_t *orig_service)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    internal async Task<int> edit(NssmServiceConfiguration original)
    {
        _ = configure(original);
        await SaveAsync().ConfigureAwait(true);
        return Status.StartsWith("配置已保存", StringComparison.Ordinal) ? 0 : 6;
    }

    [NssmUpstreamFunction("src/gui.cpp", 888, "static TCHAR *browse_filter(int message)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    public static string browse_filter(int message) => message switch { 0 => "*.exe;*.bat;*.cmd", 1 => ".", _ => "*.*" };

    [NssmUpstreamFunction("src/gui.cpp", 897, "UINT_PTR CALLBACK browse_hook(HWND dlg, UINT message, WPARAM w, LPARAM l)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    public static bool browse_hook(string message) => message.Equals("INIT", StringComparison.OrdinalIgnoreCase);

    [NssmUpstreamFunction("src/gui.cpp", 907, "void browse(HWND window, TCHAR *current, unsigned long flags, ...)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    public void browse(NssmBrowseTarget target, string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) return;
        switch (target)
        {
            case NssmBrowseTarget.Application:
                Application = selectedPath;
                if (string.IsNullOrWhiteSpace(Directory)) Directory = Path.GetDirectoryName(selectedPath) ?? string.Empty;
                break;
            case NssmBrowseTarget.Directory: Directory = selectedPath; break;
            case NssmBrowseTarget.Stdin: Stdin = selectedPath; break;
            case NssmBrowseTarget.Stdout: Stdout = selectedPath; if (string.IsNullOrWhiteSpace(Stderr)) Stderr = selectedPath; break;
            case NssmBrowseTarget.Stderr: Stderr = selectedPath; break;
            case NssmBrowseTarget.Hook: SelectedHookCommand = selectedPath; set_hook_tab(Array.IndexOf(GuiHookEvents, SelectedHookEvent), AvailableHookActions.IndexOf(SelectedHookAction), true); break;
        }
    }

    [NssmUpstreamFunction("src/gui.cpp", 961, "INT_PTR CALLBACK tab_dlg(HWND tab, UINT message, WPARAM w, LPARAM l)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    public bool tab_dlg(string message, string control, bool enabled)
    {
        if (message.Equals("INIT", StringComparison.OrdinalIgnoreCase)) return true;
        if (!message.Equals("COMMAND", StringComparison.OrdinalIgnoreCase)) return false;
        if (control.Equals("AffinityAll", StringComparison.OrdinalIgnoreCase)) set_affinity_enabled(!enabled);
        else if (control.Equals("Rotate", StringComparison.OrdinalIgnoreCase)) set_rotation_enabled(enabled);
        else if (control.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)) set_logon_enabled(true, false);
        else if (control.Equals("VirtualService", StringComparison.OrdinalIgnoreCase)) set_logon_enabled(false, false);
        else if (control.Equals("Account", StringComparison.OrdinalIgnoreCase)) set_logon_enabled(false, true);
        else return false;
        return true;
    }

    [NssmUpstreamFunction("src/gui.cpp", 1090, "INT_PTR CALLBACK nssm_dlg(HWND window, UINT message, WPARAM w, LPARAM l)", "NssmGuiTranslationTests.frontend_rewrite_matches_gui_contract", FrontendRewrite = true)]
    internal async Task<int> nssm_dlg(string message)
    {
        if (message.Equals("INIT", StringComparison.OrdinalIgnoreCase)) { centre_window(); return 1; }
        if (message.Equals("INSTALL", StringComparison.OrdinalIgnoreCase)) return await install().ConfigureAwait(true);
        if (message.Equals("EDIT", StringComparison.OrdinalIgnoreCase) && _loaded is not null) return await edit(_loaded).ConfigureAwait(true);
        if (message.Equals("REMOVE", StringComparison.OrdinalIgnoreCase)) return await remove().ConfigureAwait(true);
        return 0;
    }

    private List<NssmHook> ParseHookDrafts()
    {
        try { return ParseHooks().ToList(); }
        catch (ArgumentException) { return []; }
    }

    private void LoadSelectedHook()
    {
        SelectedHookCommand = ParseHookDrafts().FirstOrDefault(hook => hook.Event.Equals(SelectedHookEvent, StringComparison.OrdinalIgnoreCase) && hook.Action.Equals(SelectedHookAction, StringComparison.OrdinalIgnoreCase))?.Command ?? string.Empty;
    }
}

internal static class NssmGuiListExtensions
{
    internal static int IndexOf<T>(this IReadOnlyList<T> values, T value)
    {
        for (var index = 0; index < values.Count; index++) if (EqualityComparer<T>.Default.Equals(values[index], value)) return index;
        return -1;
    }
}
