using System.Diagnostics;

namespace NssmManager.Windows;

public static class WindowsProcessSignals
{
    public static void EnsureConsole() => NativeMethods.AllocConsole();

    public static void SendConsoleControlC(uint processId)
    {
        NativeMethods.FreeConsole();
        if (!NativeMethods.AttachConsole(processId)) return;
        try
        {
            NativeMethods.SetConsoleCtrlHandler(IntPtr.Zero, true);
            NativeMethods.GenerateConsoleCtrlEvent(NativeMethods.CtrlCEvent, 0);
        }
        finally
        {
            NativeMethods.FreeConsole();
            NativeMethods.SetConsoleCtrlHandler(IntPtr.Zero, false);
        }
    }

    public static void CloseWindows(uint processId)
    {
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out var owner);
            if (owner == processId) NativeMethods.PostMessage(window, NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    public static void QuitThreads(Process process)
    {
        foreach (ProcessThread thread in process.Threads)
            NativeMethods.PostThreadMessage(unchecked((uint)thread.Id), NativeMethods.WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Suspend(Process process) => ChangeSuspensionTree(process, true);
    public static void Resume(Process process) => ChangeSuspensionTree(process, false);

    private static void ChangeSuspensionTree(Process root, bool suspend)
    {
        DateTime? rootStarted;
        try { rootStarted = root.StartTime.ToUniversalTime(); }
        catch { rootStarted = null; }
        foreach (var processId in ProcessTree(root.Id))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (rootStarted.HasValue && process.StartTime.ToUniversalTime() < rootStarted.Value) continue;
                ChangeSuspension(process, suspend);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private static int[] ProcessTree(int rootProcessId)
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.ToolhelpSnapshotProcess, 0);
        if (snapshot == new IntPtr(-1)) return [rootProcessId];
        try
        {
            var children = new Dictionary<uint, List<uint>>();
            var entry = new NativeMethods.ProcessEntry32 { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.ProcessEntry32>(), ExecutableFile = "" };
            if (NativeMethods.Process32First(snapshot, ref entry))
            {
                do
                {
                    if (!children.TryGetValue(entry.ParentProcessId, out var values)) children.Add(entry.ParentProcessId, values = []);
                    values.Add(entry.ProcessId);
                    entry.Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.ProcessEntry32>();
                }
                while (NativeMethods.Process32Next(snapshot, ref entry));
            }
            var result = new List<int>();
            var visited = new HashSet<uint>();
            Add(unchecked((uint)rootProcessId));
            return result.ToArray();

            void Add(uint processId)
            {
                if (!visited.Add(processId)) return;
                result.Add(unchecked((int)processId));
                if (children.TryGetValue(processId, out var descendants)) foreach (var child in descendants) Add(child);
            }
        }
        finally { NativeMethods.CloseHandle(snapshot); }
    }

    private static void ChangeSuspension(Process process, bool suspend)
    {
        foreach (ProcessThread thread in process.Threads)
        {
            var handle = NativeMethods.OpenThread(NativeMethods.ThreadSuspendResume, false, unchecked((uint)thread.Id));
            if (handle == IntPtr.Zero) continue;
            try { if (suspend) NativeMethods.SuspendThread(handle); else while (NativeMethods.ResumeThread(handle) > 1) { } }
            finally { NativeMethods.CloseHandle(handle); }
        }
    }
}
