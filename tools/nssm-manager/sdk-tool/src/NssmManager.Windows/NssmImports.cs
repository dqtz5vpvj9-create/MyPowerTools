using System.Runtime.InteropServices;
using NssmManager.Contracts;

namespace NssmManager.Windows;

/// <summary>Direct translation of imports.cpp using NativeLibrary.</summary>
public static class NssmImports
{
    private const int ErrorModNotFound = 126;
    private const int ErrorProcNotFound = 127;
    private static IntPtr _kernel32;
    private static IntPtr _advapi32;

    public static IntPtr AttachConsole { get; private set; }
    public static IntPtr QueryFullProcessImageName { get; private set; }
    public static IntPtr SleepConditionVariableCs { get; private set; }
    public static IntPtr WakeConditionVariable { get; private set; }
    public static IntPtr CreateWellKnownSid { get; private set; }
    public static IntPtr IsWellKnownSid { get; private set; }

    [NssmUpstreamFunction("src/imports.cpp", 12, "HMODULE get_dll(const TCHAR *dll, unsigned long *error)", "NssmImportsTests.get_dll_reports_win32_loader_error")]
    public static IntPtr get_dll(string dll, out uint error)
    {
        error = 0;
        if (!OperatingSystem.IsWindows())
        {
            error = ErrorModNotFound;
            return IntPtr.Zero;
        }

        var library = LoadLibrary(dll);
        if (library != IntPtr.Zero) return library;
        error = checked((uint)Marshal.GetLastPInvokeError());
        return IntPtr.Zero;
    }

    [NssmUpstreamFunction("src/imports.cpp", 24, "FARPROC get_import(HMODULE library, const char *function, unsigned long *error)", "NssmImportsTests.get_import_reports_missing_export")]
    public static IntPtr get_import(IntPtr library, string function, out uint error)
    {
        error = 0;
        var address = library == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(library, function);
        if (address != IntPtr.Zero) return address;
        error = checked((uint)Marshal.GetLastPInvokeError());
        if (error == 0) error = ErrorProcNotFound;
        return IntPtr.Zero;
    }

    [NssmUpstreamFunction("src/imports.cpp", 42, "int get_imports()", "NssmImportsTests.get_imports_matches_optional_import_contract")]
    public static int get_imports()
    {
        free_imports();
        _kernel32 = get_dll("kernel32.dll", out var error);
        if (_kernel32 != IntPtr.Zero)
        {
            AttachConsole = get_import(_kernel32, "AttachConsole", out error);
            if (AttachConsole == IntPtr.Zero && error != ErrorProcNotFound) return 2;
            QueryFullProcessImageName = get_import(_kernel32, "QueryFullProcessImageNameW", out error);
            if (QueryFullProcessImageName == IntPtr.Zero && error != ErrorProcNotFound) return 3;
            SleepConditionVariableCs = get_import(_kernel32, "SleepConditionVariableCS", out error);
            if (SleepConditionVariableCs == IntPtr.Zero && error != ErrorProcNotFound) return 4;
            WakeConditionVariable = get_import(_kernel32, "WakeConditionVariable", out error);
            if (WakeConditionVariable == IntPtr.Zero && error != ErrorProcNotFound) return 5;
        }
        else if (error != ErrorModNotFound) return 1;

        _advapi32 = get_dll("advapi32.dll", out error);
        if (_advapi32 != IntPtr.Zero)
        {
            CreateWellKnownSid = get_import(_advapi32, "CreateWellKnownSid", out error);
            if (CreateWellKnownSid == IntPtr.Zero && error != ErrorProcNotFound) return 7;
            IsWellKnownSid = get_import(_advapi32, "IsWellKnownSid", out error);
            if (IsWellKnownSid == IntPtr.Zero && error != ErrorProcNotFound) return 8;
        }
        else if (error != ErrorModNotFound) return 6;

        return 0;
    }

    [NssmUpstreamFunction("src/imports.cpp", 87, "void free_imports()", "NssmImportsTests.free_imports_zeros_every_slot")]
    public static void free_imports()
    {
        if (_kernel32 != IntPtr.Zero) FreeLibrary(_kernel32);
        if (_advapi32 != IntPtr.Zero) FreeLibrary(_advapi32);
        _kernel32 = IntPtr.Zero;
        _advapi32 = IntPtr.Zero;
        AttachConsole = IntPtr.Zero;
        QueryFullProcessImageName = IntPtr.Zero;
        SleepConditionVariableCs = IntPtr.Zero;
        WakeConditionVariable = IntPtr.Zero;
        CreateWellKnownSid = IntPtr.Zero;
        IsWellKnownSid = IntPtr.Zero;
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string fileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}
