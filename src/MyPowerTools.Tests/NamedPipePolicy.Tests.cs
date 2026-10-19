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

    [Fact]
    public async Task Raw_pipe_replacement_accepts_repeated_clients_and_inherits_low_integrity_label()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pipeName = $"mpt-raw-policy-test-{Guid.NewGuid():N}";
        var listener = MptNamedPipePolicy.CreateServer(pipeName);
        try
        {
            for (var index = 0; index < 8; index++)
            {
                await using var client = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.InOut, MptNamedPipePolicy.ClientOptions);
                await using var accepted = listener;
                await Task.WhenAll(
                    accepted.WaitForConnectionAsync(deadline.Token),
                    client.ConnectAsync(deadline.Token));

                // Match ScreenEase: keep the accepted instance alive while replacing
                // the listener, including after the original first instance is gone.
                listener = MptNamedPipePolicy.CreateAdditionalServer(pipeName);
                Assert.Equal(MptNamedPipePolicy.LowIntegrityLabelSddl, ReadIntegrityLabel(accepted.SafePipeHandle));
                Assert.Equal(MptNamedPipePolicy.LowIntegrityLabelSddl, ReadIntegrityLabel(listener.SafePipeHandle));

                byte[] payload = [(byte)index];
                var received = new byte[1];
                // With zero-sized Windows pipe buffers, a write can wait for its reader.
                await Task.WhenAll(
                    client.WriteAsync(payload, deadline.Token).AsTask(),
                    accepted.ReadExactlyAsync(received, deadline.Token).AsTask());
                Assert.Equal(payload, received);
                var echoed = new byte[1];
                await Task.WhenAll(
                    accepted.WriteAsync(received, deadline.Token).AsTask(),
                    client.ReadExactlyAsync(echoed, deadline.Token).AsTask());
                Assert.Equal(payload, echoed);
            }
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public void Additional_pipe_instance_rejects_first_instance_options()
    {
        Assert.Throws<ArgumentException>(() => MptNamedPipePolicy.CreateAdditionalServer(
            "unused", options: PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance));
        Assert.Throws<ArgumentOutOfRangeException>(() => MptNamedPipePolicy.CreateAdditionalServer(
            "unused", maxInstances: 1));
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
