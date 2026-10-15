using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MyPowerTools.Platform.Windows;

/// <summary>Registers the product shortcut for protocol-activated desktop toasts.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsNotificationShortcut
{
    public const string AppUserModelId = "MyPowerTools.Desktop";
    // A stable stub CLSID is sufficient for protocol activation. Do not register a COM server.
    public static readonly Guid ToastActivatorId = new("530F5F92-9B32-45C6-9473-A2A761D26137");
    private static readonly Guid AppModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    public static void RegisterInstalledShortcut()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", "MyPowerTools", "MyPowerTools.lnk");
        if (File.Exists(path)) Register(path);
    }

    public static void Register(string shortcutPath)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(
            Path.GetFullPath(shortcutPath), IntPtr.Zero, 2, ref iid, out var store));
        try
        {
            Set(store, 5, AppUserModelId);
            Set(store, 26, ToastActivatorId);
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    private static void Set(IPropertyStore store, uint id, object value)
    {
        var key = new PropertyKey { FormatId = AppModelFormat, Id = id };
        var variant = new PropVariant();
        try
        {
            if (value is Guid guid)
            {
                variant.Type = 72; // VT_CLSID
                variant.Pointer = Marshal.AllocCoTaskMem(16);
                Marshal.StructureToPtr(guid, variant.Pointer, false);
            }
            else
            {
                variant.Type = 31; // VT_LPWSTR
                variant.Pointer = Marshal.StringToCoTaskMemUni((string)value);
            }
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref variant));
        }
        finally { if (variant.Pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(variant.Pointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid FormatId; public uint Id; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext,
        uint flags, ref Guid iid, out IPropertyStore store);
}
