using System.Runtime.InteropServices;

namespace MyPowerTools.Platform.Mac;

internal static class MacNative
{
    private const string LibraryName = "MptMacNative";

    internal const string MissingLibraryMessage =
        "The macOS native capability library is missing from the application bundle.";

    /// <summary>Native status codes shared with MptMacNative.mm.</summary>
    internal const int NotificationOsUnsupported = -1;
    internal const int NotificationNoBundle = 2;
    internal const int NotificationUnavailable = 3;
    internal const int NotificationPermissionDenied = 4;
    internal const int NotificationDeliveryFailed = 5;
    internal const int NotificationTimedOut = 6;

    internal const int NotificationAuthorizationUnavailable = -1;
    internal const int NotificationAuthorizationNotDetermined = 0;
    internal const int NotificationAuthorizationDenied = 1;
    internal const int NotificationAuthorizationAuthorized = 2;
    internal const int NotificationAuthorizationProvisional = 3;

    [DllImport(LibraryName, EntryPoint = "mpt_notification_authorization_status", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int GetNotificationAuthorizationStatus();

    [DllImport(LibraryName, EntryPoint = "mpt_notification_publish", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int PublishNotification(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string identifier,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string body,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string activationUri);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void TrayActionCallback(nint context, nint actionId);

    [DllImport(LibraryName, EntryPoint = "mpt_status_item_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint CreateStatusItem(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string toolTip,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string iconPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string menuJson,
        TrayActionCallback callback,
        nint context);

    [DllImport(LibraryName, EntryPoint = "mpt_status_item_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void DestroyStatusItem(nint handle);

    [DllImport(LibraryName, EntryPoint = "mpt_status_item_update_quota", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int UpdateStatusItemQuota(
        nint handle,
        int remainingPercent,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string toolTip);

    [DllImport(LibraryName, EntryPoint = "mpt_pasteboard_read_png", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadPasteboardPng(
        out nint bytes,
        out nuint length,
        out int width,
        out int height);

    [DllImport(LibraryName, EntryPoint = "mpt_pasteboard_write_text", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int WritePasteboardText(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibraryName, EntryPoint = "mpt_keychain_save", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SaveKeychainSecret(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string service,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string account,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibraryName, EntryPoint = "mpt_keychain_read", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ReadKeychainSecret(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string service,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string account,
        out nint value);

    [DllImport(LibraryName, EntryPoint = "mpt_keychain_delete", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int DeleteKeychainSecret(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string service,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string account);

    [DllImport(LibraryName, EntryPoint = "mpt_free", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Free(nint value);
}
