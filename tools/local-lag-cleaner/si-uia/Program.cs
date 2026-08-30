using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Forms;
using Condition = System.Windows.Automation.Condition;

namespace SiUiaHost;

internal static class Program
{
    private const string FindCaption = "Find Handles or DLLs";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                PrintHelp();
                return 2;
            }

            if (!IsElevated())
            {
                Console.Error.WriteLine("ERROR: SiUiaHost must run elevated. UIPI blocks a medium-IL process from driving an Administrator System Informer window.");
                return 3;
            }

            return args[0].ToLowerInvariant() switch
            {
                "inspect" => CmdInspect(),
                "find" => CmdFind(ParseOptions(args.Skip(1))),
                "copy-selected" => CmdCopySelected(),
                "close-selected" => CmdCloseSelected(ParseOptions(args.Skip(1))),
                "find-and-close" => CmdFindAndClose(ParseOptions(args.Skip(1))),
                _ => throw new ArgumentException("Unknown command: " + args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            SiUiaHost — elevated UI Automation driver for System Informer.

            Commands:
              inspect
              find --type File --query <text> [--timeout 180]
              copy-selected
              close-selected --yes [--timeout 60]
              find-and-close --type File --query <text> --yes [--timeout 180]

            Empty --query is refused (that searches every handle on the machine).
            """);
    }

    private sealed class Options
    {
        public string Type { get; set; } = "File";
        public string Query { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 180;
        public bool Yes { get; set; }
    }

    private static Options ParseOptions(IEnumerable<string> args)
    {
        var o = new Options();
        using var it = args.GetEnumerator();
        while (it.MoveNext())
        {
            switch (it.Current)
            {
                case "--type":
                    o.Type = Next(it, "--type");
                    break;
                case "--query":
                    o.Query = Next(it, "--query");
                    break;
                case "--timeout":
                    o.TimeoutSeconds = int.Parse(Next(it, "--timeout"));
                    break;
                case "--yes":
                    o.Yes = true;
                    break;
                default:
                    throw new ArgumentException("Unknown option: " + it.Current);
            }
        }

        return o;
    }

    private static string Next(IEnumerator<string> it, string name)
    {
        if (!it.MoveNext())
            throw new ArgumentException(name + " requires a value");
        return it.Current;
    }

    private static int CmdInspect()
    {
        var si = RequireSi();
        RestoreAndFocus(si);
        Thread.Sleep(400);
        Console.WriteLine($"PID {si.Id}  title='{si.MainWindowTitle}'");
        foreach (var root in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>())
        {
            int pid;
            try { pid = root.Current.ProcessId; }
            catch { continue; }
            if (pid != si.Id)
                continue;
            DumpTree(root, 0, 6);
        }

        return 0;
    }

    private static int CmdFind(Options o)
    {
        ValidateQuery(o);
        var si = RequireSi();
        var find = OpenFindDialog(si);
        ConfigureAndSearch(find, o);
        var count = WaitSearchFinished(find, o.TimeoutSeconds);
        Console.WriteLine($"SEARCH_DONE results={count} title='{SafeName(find)}'");
        return 0;
    }

    private static int CmdCopySelected()
    {
        var si = RequireSi();
        var find = FindExistingFindDialog(si) ?? throw new InvalidOperationException("Find Handles dialog is not open.");
        RestoreElement(find);
        Thread.Sleep(200);
        var tree = FindTree(find);
        if (tree is not null)
            tree.SetFocus();
        else
            find.SetFocus();
        Thread.Sleep(150);
        SendChord(Keys.ControlKey, Keys.A);
        Thread.Sleep(200);
        SendChord(Keys.ControlKey, Keys.C);
        Thread.Sleep(300);
        var text = Clipboard.GetText() ?? "";
        Console.WriteLine($"COPY_DONE chars={text.Length}");
        Console.WriteLine(text);
        return 0;
    }

    private static int CmdCloseSelected(Options o)
    {
        if (!o.Yes)
            throw new InvalidOperationException("close-selected requires --yes");
        var si = RequireSi();
        var find = FindExistingFindDialog(si) ?? throw new InvalidOperationException("Find Handles dialog is not open.");
        CloseSelectedResults(find, o);
        return 0;
    }

    private static int CmdFindAndClose(Options o)
    {
        if (!o.Yes)
            throw new InvalidOperationException("find-and-close requires --yes");
        ValidateQuery(o);
        var si = RequireSi();
        var find = OpenFindDialog(si);
        ConfigureAndSearch(find, o);
        var count = WaitSearchFinished(find, o.TimeoutSeconds);
        Console.WriteLine($"SEARCH_DONE results={count} title='{SafeName(find)}'");
        if (count <= 0)
        {
            Console.WriteLine("CLOSE_SKIPPED no results");
            return 0;
        }

        CloseSelectedResults(find, o);
        return 0;
    }

    private static void ValidateQuery(Options o)
    {
        if (string.IsNullOrWhiteSpace(o.Query) || o.Query.Trim().Length < 3)
            throw new InvalidOperationException("Query must be at least 3 characters. An empty search makes System Informer walk every handle.");
        if (o.Type.Equals("Everything", StringComparison.OrdinalIgnoreCase) && o.Query.Trim().Length < 4)
            throw new InvalidOperationException("Refuse Everything search with a short query.");
    }

    private static Process RequireSi()
    {
        var matches = Process.GetProcessesByName("SystemInformer")
            .Where(p =>
            {
                try { return p.MainWindowHandle != IntPtr.Zero; }
                catch { return false; }
            })
            .ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException("System Informer window not found. Start the Canary build as Administrator first.");

        var chosen = matches.FirstOrDefault(IsProcessElevated) ?? matches[0];
        if (!IsProcessElevated(chosen))
            throw new InvalidOperationException("System Informer is running but its token is not elevated.");

        var titles = string.Join(" | ", matches.Select(p =>
        {
            try { return $"PID {p.Id} '{p.MainWindowTitle}'"; }
            catch { return $"PID {p.Id}"; }
        }));
        Console.WriteLine("SI " + titles);
        if (!titles.Contains("++", StringComparison.Ordinal) &&
            !titles.Contains("Administrator", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("WARN: no visible ++ / Administrator title. KphLevelMax is not confirmed from the window text.");
        return chosen;
    }

    private static bool IsProcessElevated(Process process)
    {
        if (!Native.OpenProcessToken(process.Handle, 0x0008, out var token))
            return false;
        try
        {
            var elev = 0;
            if (!Native.GetTokenInformation(token, 20, ref elev, sizeof(int), out _))
                return false;
            return elev != 0;
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    private static AutomationElement OpenFindDialog(Process si)
    {
        var existing = FindExistingFindDialog(si);
        if (existing is not null)
        {
            RestoreElement(existing);
            Console.WriteLine("FIND_DIALOG reuse");
            return existing;
        }

        RestoreAndFocus(si);
        Thread.Sleep(200);
        var main = FindMainWindow(si);
        if (main != IntPtr.Zero)
        {
            Native.ShowWindow(main, 9);
            Native.SetForegroundWindow(main);
            Native.PostMessage(main, Native.WmCommand, new IntPtr(Native.IdFindHandles), IntPtr.Zero);
            Console.WriteLine("FIND_DIALOG posted WM_COMMAND");
        }
        else
        {
            SendChord(Keys.ControlKey, Keys.F);
            Console.WriteLine("FIND_DIALOG fallback Ctrl+F");
        }

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            existing = FindExistingFindDialog(si);
            if (existing is not null)
            {
                Console.WriteLine("FIND_DIALOG opened");
                return existing;
            }

            Thread.Sleep(150);
        }

        throw new TimeoutException("Timed out waiting for 'Find Handles or DLLs'.");
    }

    private static IntPtr FindMainWindow(Process si)
    {
        var found = IntPtr.Zero;
        Native.EnumWindows((h, l) =>
        {
            Native.GetWindowThreadProcessId(h, out var procId);
            if (procId != (uint)si.Id)
                return true;
            var cls = new System.Text.StringBuilder(256);
            Native.GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() == "MainWindowClassName")
            {
                found = h;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static AutomationElement? FindExistingFindDialog(Process si)
    {
        foreach (var root in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>())
        {
            try
            {
                if (root.Current.ProcessId != si.Id)
                    continue;
                var name = root.Current.Name ?? "";
                if (name.StartsWith(FindCaption, StringComparison.OrdinalIgnoreCase))
                    return root;
            }
            catch
            {
                // The tree can change while we walk it.
            }
        }

        return null;
    }

    private static void ConfigureAndSearch(AutomationElement find, Options o)
    {
        RestoreElement(find);
        Thread.Sleep(200);

        var combo = find.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox))
            ?? throw new InvalidOperationException("Type combo box not found.");
        SelectCombo(combo, o.Type);

        var edit = FindSearchEdit(find) ?? throw new InvalidOperationException("Search edit box not found.");
        SetValue(edit, o.Query);
        Console.WriteLine($"SEARCH_SET type={o.Type} query={o.Query}");

        var findButton = FindButton(find, "Find") ?? throw new InvalidOperationException("Find button not found.");
        Invoke(findButton);
        Console.WriteLine("SEARCH_STARTED");
    }

    private static int WaitSearchFinished(AutomationElement find, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            DismissWarnings(find);
            var title = SafeName(find);
            var m = Regex.Match(title, @"\((\d+)\s+results?\)", RegexOptions.IgnoreCase);
            if (m.Success && FindButton(find, "Find") is not null)
                return int.Parse(m.Groups[1].Value);

            var cancel = FindButton(find, "Cancel");
            if (cancel is null && FindButton(find, "Find") is not null && title.Equals(FindCaption, StringComparison.OrdinalIgnoreCase))
            {
                // Search finished with the default title and zero/unknown count.
                return 0;
            }

            Thread.Sleep(400);
        }

        throw new TimeoutException($"Search did not finish within {timeoutSeconds}s. Last title='{SafeName(find)}'");
    }

    private static void CloseSelectedResults(AutomationElement find, Options o)
    {
        RestoreElement(find);
        Thread.Sleep(200);
        var tree = FindTree(find);
        if (tree is not null)
            tree.SetFocus();
        else
            find.SetFocus();
        Thread.Sleep(150);
        SendChord(Keys.ControlKey, Keys.A);
        Thread.Sleep(1500);
        SendKeysStroke(Keys.Delete);
        Console.WriteLine("CLOSE_SENT Ctrl+A, Del");

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, o.TimeoutSeconds));
        var confirmed = false;
        while (DateTime.UtcNow < deadline)
        {
            if (TryConfirmClose())
            {
                confirmed = true;
                Console.WriteLine("CLOSE_CONFIRMED");
            }

            DismissWarnings(find);
            Thread.Sleep(250);

            if (confirmed && FindTaskDialog() is null)
                break;
        }

        if (!confirmed)
            throw new TimeoutException("Close confirmation dialog did not appear. TreeNew may not have had focus or any selection.");

        Console.WriteLine($"CLOSE_DONE title='{SafeName(find)}'");
    }

    private static bool TryConfirmClose()
    {
        var dialog = FindTaskDialog();
        if (dialog is null)
            return false;
        var name = SafeName(dialog);
        if (name.Contains("too large", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Unable to search", StringComparison.OrdinalIgnoreCase))
        {
            _ = ClickButton(dialog, "OK") || ClickButton(dialog, "确定");
            throw new InvalidOperationException("System Informer refused the search: handle table is too large.");
        }

        if (!(name.Contains("close", StringComparison.OrdinalIgnoreCase) ||
              name.Contains("关闭", StringComparison.Ordinal)))
        {
            return false;
        }

        if (!(ClickButton(dialog, "Yes") || ClickButton(dialog, "是")))
            throw new InvalidOperationException("Found the close confirmation but could not invoke Yes.");
        return true;
    }

    private static void DismissWarnings(AutomationElement find)
    {
        foreach (var root in TopLevelWindows(find.Current.ProcessId))
        {
            var name = SafeName(root);
            if (name.Contains("too large", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("System Informer refused the search: handle table is too large.");
            if (name.Contains("Unable to close", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Do you want to continue", StringComparison.OrdinalIgnoreCase))
            {
                _ = ClickButton(root, "Yes") || ClickButton(root, "是") || ClickButton(root, "Continue") || ClickButton(root, "继续");
                Console.WriteLine("WARN continue-status: " + name);
            }
        }
    }

    private static AutomationElement? FindTaskDialog()
    {
        foreach (var root in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>())
        {
            try
            {
                var name = root.Current.Name ?? "";
                var cls = root.Current.ClassName ?? "";
                if (cls.Contains("TaskDialog", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Do you want to close", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("System Informer", StringComparison.OrdinalIgnoreCase) && name.Contains("close", StringComparison.OrdinalIgnoreCase))
                {
                    if (FindButton(root, "Yes") is not null || FindButton(root, "是") is not null)
                        return root;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static IEnumerable<AutomationElement> TopLevelWindows(int pid)
    {
        foreach (var root in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>())
        {
            int id;
            try { id = root.Current.ProcessId; }
            catch { continue; }
            if (id == pid)
                yield return root;
        }
    }

    private static AutomationElement? FindSearchEdit(AutomationElement find)
    {
        var edits = find.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        return edits.Count > 0 ? edits[0] : null;
    }

    private static AutomationElement? FindTree(AutomationElement find)
    {
        return find.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "PhTreeNew"))
            ?? find.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree))
            ?? find.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid))
            ?? find.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List));
    }

    private static AutomationElement? FindButton(AutomationElement root, string name)
    {
        var cond = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, name));
        return root.FindFirst(TreeScope.Descendants, cond);
    }

    private static bool ClickButton(AutomationElement root, string name)
    {
        var button = FindButton(root, name);
        if (button is null)
            return false;
        Invoke(button);
        return true;
    }

    private static void SelectCombo(AutomationElement combo, string value)
    {
        combo.SetFocus();
        if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expObj) && expObj is ExpandCollapsePattern exp)
        {
            if (exp.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
                exp.Expand();
            Thread.Sleep(150);
        }

        var item = combo.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, value));
        if (item is not null && item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selObj) && selObj is SelectionItemPattern sel)
        {
            sel.Select();
            return;
        }

        SetValue(combo, value);
    }

    private static void SetValue(AutomationElement element, string value)
    {
        element.SetFocus();
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp)
        {
            vp.SetValue(value);
            return;
        }

        SendChord(Keys.ControlKey, Keys.A);
        Thread.Sleep(50);
        SendKeys.SendWait(EscapeSendKeys(value));
    }

    private static void Invoke(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invObj) && invObj is InvokePattern inv)
        {
            inv.Invoke();
            return;
        }

        throw new InvalidOperationException("Element is not invokable: " + SafeName(element));
    }

    private static void DumpTree(AutomationElement el, int depth, int maxDepth)
    {
        string name, ct, cls;
        try
        {
            name = el.Current.Name ?? "";
            ct = el.Current.ControlType.ProgrammaticName;
            cls = el.Current.ClassName ?? "";
        }
        catch
        {
            return;
        }

        Console.WriteLine($"{new string(' ', depth * 2)}[{ct}] class={cls} name='{Trim(name)}'");
        if (depth >= maxDepth)
            return;
        AutomationElementCollection? kids = null;
        try { kids = el.FindAll(TreeScope.Children, Condition.TrueCondition); }
        catch { return; }
        foreach (AutomationElement kid in kids)
            DumpTree(kid, depth + 1, maxDepth);
    }

    private static string Trim(string s) => s.Length <= 160 ? s : s[..160] + "...";

    private static string SafeName(AutomationElement el)
    {
        try { return el.Current.Name ?? ""; }
        catch { return ""; }
    }

    private static string EscapeSendKeys(string s) =>
        Regex.Replace(s, @"[+^%~(){}]", m => "{" + m.Value + "}");

    private static void RestoreAndFocus(Process si)
    {
        if (si.MainWindowHandle == IntPtr.Zero)
            si.Refresh();
        var hwnd = si.MainWindowHandle;
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("System Informer has no main window handle.");
        if (Native.IsIconic(hwnd))
            Native.ShowWindow(hwnd, 9);
        else
            Native.ShowWindow(hwnd, 5);
        Native.BringWindowToTop(hwnd);
        Native.SetForegroundWindow(hwnd);
    }

    private static void RestoreElement(AutomationElement el)
    {
        try
        {
            var hwnd = new IntPtr(el.Current.NativeWindowHandle);
            if (hwnd != IntPtr.Zero)
            {
                if (Native.IsIconic(hwnd))
                    Native.ShowWindow(hwnd, 9);
                Native.SetForegroundWindow(hwnd);
            }

            el.SetFocus();
        }
        catch
        {
        }
    }

    private static void SendChord(Keys modifier, Keys key)
    {
        Native.keybd_event((byte)modifier, 0, 0, UIntPtr.Zero);
        Thread.Sleep(20);
        Native.keybd_event((byte)key, 0, 0, UIntPtr.Zero);
        Thread.Sleep(20);
        Native.keybd_event((byte)key, 0, Native.KeyeventfKeyup, UIntPtr.Zero);
        Native.keybd_event((byte)modifier, 0, Native.KeyeventfKeyup, UIntPtr.Zero);
    }

    private static void SendKeysStroke(Keys key)
    {
        Native.keybd_event((byte)key, 0, 0, UIntPtr.Zero);
        Thread.Sleep(20);
        Native.keybd_event((byte)key, 0, Native.KeyeventfKeyup, UIntPtr.Zero);
    }

    private static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static class Native
    {
        public const uint WmCommand = 0x0111;
        public const int IdFindHandles = 10082;
        public const uint KeyeventfKeyup = 0x0002;
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint procId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, ref int tokenInformation, int tokenInformationLength, out int returnLength);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    }
}
