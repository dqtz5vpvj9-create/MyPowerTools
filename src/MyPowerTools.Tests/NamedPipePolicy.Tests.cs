using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Microsoft.Win32.SafeHandles;
using MyPowerTools.Ipc;

namespace MyPowerTools.Tests;

public sealed class NamedPipePolicyTests
{
    [Fact]
    public void Kestrel_policy_disables_elevation_sensitive_current_user_check()
    {
        var options = new NamedPipeTransportOptions();

        MptNamedPipePolicy.Configure(options);

        Assert.False(options.CurrentUserOnly);
        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(options.PipeSecurity);
            AssertWorldDescriptor(options.PipeSecurity!);
        }
    }

    [Fact]
    public void Windows_pipe_descriptor_allows_world_and_low_integrity_clients()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        AssertWorldDescriptor(MptNamedPipePolicy.CreatePipeSecurity());
        Assert.Equal("S:(ML;;NW;;;LW)", MptNamedPipePolicy.LowIntegrityLabelSddl);

        using var server = MptNamedPipePolicy.CreateServer(
            $"mpt-policy-test-{Guid.NewGuid():N}",
            maxInstances: 1);
        Assert.False(server.IsConnected);
    }

    [Fact]
    public void Kestrel_factory_creates_multiple_same_name_instances_with_low_integrity_label()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var options = new NamedPipeTransportOptions();
        MptNamedPipePolicy.Configure(options);
        var factory = Assert.IsType<Func<CreateNamedPipeServerStreamContext, NamedPipeServerStream>>(
            options.CreateNamedPipeServerStream);
        var pipeName = $"mpt-kestrel-policy-test-{Guid.NewGuid():N}";

        using var first = factory(new CreateNamedPipeServerStreamContext
        {
            NamedPipeEndPoint = new NamedPipeEndPoint(pipeName),
            PipeOptions = PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.FirstPipeInstance,
            PipeSecurity = options.PipeSecurity
        });
        using var second = factory(new CreateNamedPipeServerStreamContext
        {
            NamedPipeEndPoint = new NamedPipeEndPoint(pipeName),
            PipeOptions = PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            PipeSecurity = options.PipeSecurity
        });

        Assert.Equal(MptNamedPipePolicy.LowIntegrityLabelSddl, ReadIntegrityLabel(first.SafePipeHandle));
        Assert.Equal(MptNamedPipePolicy.LowIntegrityLabelSddl, ReadIntegrityLabel(second.SafePipeHandle));
    }

    [SupportedOSPlatform("windows")]
    private static void AssertWorldDescriptor(PipeSecurity security)
    {
        Assert.True(security.AreAccessRulesProtected);

        var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null);
        var worldFullControlRule = security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                targetType: typeof(SecurityIdentifier))
            .OfType<PipeAccessRule>()
            .SingleOrDefault(rule =>
                rule.AccessControlType == AccessControlType.Allow &&
                worldSid.Equals(rule.IdentityReference) &&
                (rule.PipeAccessRights & PipeAccessRights.FullControl) == PipeAccessRights.FullControl);

        Assert.NotNull(worldFullControlRule);
    }

    [SupportedOSPlatform("windows")]
    private static string ReadIntegrityLabel(SafePipeHandle pipeHandle)
    {
        const int seKernelObject = 6;
        const uint labelSecurityInformation = 0x10;

        var error = NativeMethods.GetSecurityInfo(
            pipeHandle,
            seKernelObject,
            labelSecurityInformation,
            out _,
            out _,
            out _,
            out _,
            out var securityDescriptor);
        if (error != 0)
        {
            throw new Win32Exception((int)error, "Could not read the named-pipe integrity label.");
        }

        try
        {
            if (!NativeMethods.ConvertSecurityDescriptorToStringSecurityDescriptor(
                    securityDescriptor,
                    stringSdRevision: 1,
                    labelSecurityInformation,
                    out var stringSecurityDescriptor,
                    out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not format the named-pipe integrity label.");
            }

            try
            {
                return Marshal.PtrToStringUni(stringSecurityDescriptor)
                    ?? throw new InvalidOperationException("The named-pipe integrity label was empty.");
            }
            finally
            {
                NativeMethods.LocalFree(stringSecurityDescriptor);
            }
        }
        finally
        {
            NativeMethods.LocalFree(securityDescriptor);
        }
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll")]
        internal static extern uint GetSecurityInfo(
            SafePipeHandle handle,
            int objectType,
            uint securityInformation,
            out IntPtr owner,
            out IntPtr group,
            out IntPtr dacl,
            out IntPtr sacl,
            out IntPtr securityDescriptor);

        [DllImport(
            "advapi32.dll",
            EntryPoint = "ConvertSecurityDescriptorToStringSecurityDescriptorW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(
            IntPtr securityDescriptor,
            uint stringSdRevision,
            uint securityInformation,
            out IntPtr stringSecurityDescriptor,
            out uint stringSecurityDescriptorLength);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr memory);
    }
}
