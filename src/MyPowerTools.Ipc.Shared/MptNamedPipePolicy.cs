using System.IO.Pipes;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;

namespace MyPowerTools.Ipc;

/// <summary>
/// Defines the named-pipe reachability policy shared by every MyPowerTools host.
/// Application-level bearer tokens remain responsible for authorizing sensitive RPCs.
/// </summary>
public static class MptNamedPipePolicy
{
    private const int SeKernelObject = 6;
    private const uint LabelSecurityInformation = 0x10;

    public const string LowIntegrityLabelSddl = "S:(ML;;NW;;;LW)";

    /// <summary>
    /// All clients use overlapped I/O and omit <see cref="PipeOptions.CurrentUserOnly"/>,
    /// which also compares elevation level on Windows.
    /// </summary>
    public const PipeOptions ClientOptions = PipeOptions.Asynchronous;

    /// <summary>
    /// Configures Kestrel named pipes for connections across Windows elevation levels.
    /// </summary>
    public static void Configure(NamedPipeTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.CurrentUserOnly = false;
        if (OperatingSystem.IsWindows())
        {
            options.PipeSecurity = CreatePipeSecurity();
            options.CreateNamedPipeServerStream = context => CreateWindowsServer(
                context.NamedPipeEndPoint.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                context.PipeOptions,
                inBufferSize: 0,
                outBufferSize: 0,
                initializeLowIntegrityLabel:
                    (context.PipeOptions & PipeOptions.FirstPipeInstance) != 0);
        }
    }

    /// <summary>
    /// Creates a server stream with the shared cross-elevation security descriptor.
    /// </summary>
    public static NamedPipeServerStream CreateServer(
        string pipeName,
        PipeDirection direction = PipeDirection.InOut,
        int maxInstances = NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode transmissionMode = PipeTransmissionMode.Byte,
        PipeOptions options = PipeOptions.Asynchronous,
        int inBufferSize = 0,
        int outBufferSize = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        var normalizedOptions = options & ~PipeOptions.CurrentUserOnly;
        if (!OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(
                pipeName,
                direction,
                maxInstances,
                transmissionMode,
                normalizedOptions,
                inBufferSize,
                outBufferSize);
        }

        return CreateWindowsServer(
            pipeName,
            direction,
            maxInstances,
            transmissionMode,
            normalizedOptions,
            inBufferSize,
            outBufferSize,
            initializeLowIntegrityLabel: true);
    }

    private static NamedPipeServerStream CreateWindowsServer(
        string pipeName,
        PipeDirection direction,
        int maxInstances,
        PipeTransmissionMode transmissionMode,
        PipeOptions options,
        int inBufferSize,
        int outBufferSize,
        bool initializeLowIntegrityLabel)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows pipe security descriptors are only available on Windows.");
        }

        var normalizedOptions = options & ~PipeOptions.CurrentUserOnly;
        var additionalAccessRights = initializeLowIntegrityLabel
            ? PipeAccessRights.TakeOwnership
            : (PipeAccessRights)0;

        var stream = NamedPipeServerStreamAcl.Create(
            pipeName,
            direction,
            maxInstances,
            transmissionMode,
            normalizedOptions,
            inBufferSize,
            outBufferSize,
            CreatePipeSecurity(),
            HandleInheritability.None,
            additionalAccessRights);

        if (!initializeLowIntegrityLabel)
        {
            // Windows copies the first named-pipe instance's mandatory label to later
            // instances. Requesting TakeOwnership again would also set the overlapping
            // FirstPipeInstance bit and make Kestrel fail while filling its listener pool.
            return stream;
        }

        try
        {
            ApplyLowIntegrityLabel(stream.SafePipeHandle);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns a protected DACL granting WorldSid full control.
    /// <see cref="CreateServer"/> applies the Low mandatory integrity label after
    /// creation because passing a label as an audit SACL requires SeSecurityPrivilege.
    /// </summary>
    public static PipeSecurity CreatePipeSecurity()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows pipe security descriptors are only available on Windows.");
        }

        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    private static void ApplyLowIntegrityLabel(SafePipeHandle pipeHandle)
    {
        if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(
                LowIntegrityLabelSddl,
                stringSdRevision: 1,
                out var securityDescriptor,
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Low integrity label.");
        }

        try
        {
            if (!NativeMethods.GetSecurityDescriptorSacl(
                    securityDescriptor,
                    out var saclPresent,
                    out var sacl,
                    out _) ||
                !saclPresent ||
                sacl == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the Low integrity label.");
            }

            var error = NativeMethods.SetSecurityInfo(
                pipeHandle,
                objectType: SeKernelObject,
                securityInformation: LabelSecurityInformation,
                owner: IntPtr.Zero,
                group: IntPtr.Zero,
                dacl: IntPtr.Zero,
                sacl);
            if (error != 0)
            {
                throw new Win32Exception((int)error, "Could not apply the Low integrity label to the named pipe.");
            }
        }
        finally
        {
            NativeMethods.LocalFree(securityDescriptor);
        }
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
            string stringSecurityDescriptor,
            uint stringSdRevision,
            out IntPtr securityDescriptor,
            out uint securityDescriptorSize);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSecurityDescriptorSacl(
            IntPtr securityDescriptor,
            [MarshalAs(UnmanagedType.Bool)] out bool saclPresent,
            out IntPtr sacl,
            [MarshalAs(UnmanagedType.Bool)] out bool saclDefaulted);

        [DllImport("advapi32.dll")]
        internal static extern uint SetSecurityInfo(
            SafePipeHandle handle,
            int objectType,
            uint securityInformation,
            IntPtr owner,
            IntPtr group,
            IntPtr dacl,
            IntPtr sacl);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr memory);
    }
}
