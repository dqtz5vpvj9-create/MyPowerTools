namespace NssmManager.Windows;

internal static class WindowsPrivileges
{
    public static void TryEnableDebugPrivilege()
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TokenAdjustPrivileges | NativeMethods.TokenQuery, out var token)) return;
        try
        {
            if (!NativeMethods.LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid)) return;
            var privileges = new NativeMethods.TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = NativeMethods.SePrivilegeEnabled };
            NativeMethods.AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { NativeMethods.CloseHandle(token); }
    }
}
