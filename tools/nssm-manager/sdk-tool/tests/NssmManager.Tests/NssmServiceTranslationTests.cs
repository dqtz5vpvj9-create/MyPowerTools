using NssmManager.Compatibility;
using NssmManager.Windows;

namespace NssmManager.Tests;

public sealed class NssmServiceTranslationTests
{
    [Fact]
    public void start_service_preserves_upstream_service_name_argument()
    {
        Assert.Equal(["sample", "one", "two"], WindowsServiceManager.service_start_arguments("sample", ["one", "two"]));
        Assert.Equal(["sample"], WindowsServiceManager.service_start_arguments("sample", null));
    }

    [Fact]
    public void control_service_requests_the_exact_scm_access_right()
    {
        Assert.Equal(0x0014u, WindowsServiceManager.service_control_access(0));
        Assert.Equal(0x0024u, WindowsServiceManager.service_control_access(1));
        Assert.Equal(0x0044u, WindowsServiceManager.service_control_access(2));
        Assert.Equal(0x0044u, WindowsServiceManager.service_control_access(3));
        Assert.Equal(0x0104u, WindowsServiceManager.service_control_access(128));
    }

    [Fact]
    public void core_service_helpers_match_upstream()
    {
        Assert.Equal(1, NssmServiceTranslation.service_control_response(0, 2));
        Assert.Equal(0, NssmServiceTranslation.service_control_response(0, 4));
        Assert.Equal(-1, NssmServiceTranslation.service_control_response(0, 1));
        Assert.Equal(1, NssmServiceTranslation.service_control_response(1, 3));
        Assert.Equal(0, NssmServiceTranslation.service_control_response(1, 1));
        Assert.Equal(-1, NssmServiceTranslation.service_control_response(2, 4));
        Assert.Equal(0, NssmServiceTranslation.service_control_response(128, 12345));

        var status = new NssmServiceStatus();
        Assert.Equal(-1, NssmServiceTranslation.await_service_control_response(0, IntPtr.Zero, ref status, 1, 1));
        Assert.Equal(-1, NssmServiceTranslation.await_service_control_response(0, IntPtr.Zero, ref status, 1));
    }

    [Fact]
    public void affinity_and_priority_match_upstream()
    {
        Assert.Equal(0, NssmServiceTranslation.affinity_string_to_mask("0-2,4,6-7", out var mask));
        Assert.Equal(0xd7UL, mask);
        Assert.Equal(0, NssmServiceTranslation.affinity_mask_to_string(mask, out var text));
        Assert.Equal("0-2,4,6,7", text);
        Assert.Equal(2, NssmServiceTranslation.affinity_string_to_mask("64", out _));
        Assert.Equal(3, NssmServiceTranslation.affinity_string_to_mask("2-", out _));
        Assert.Equal(4, NssmServiceTranslation.affinity_string_to_mask("x", out _));

        Assert.Equal(0x0000c1e0u, NssmServiceTranslation.priority_mask());
        for (var index = 0; index < 6; index++)
            Assert.Equal(index, NssmServiceTranslation.priority_constant_to_index(NssmServiceTranslation.priority_index_to_constant(index)));
        Assert.Equal(1000u, NssmServiceTranslation.throttle_milliseconds(0));
        Assert.Equal(1000u, NssmServiceTranslation.throttle_milliseconds(1));
        Assert.Equal(128000u, NssmServiceTranslation.throttle_milliseconds(8));
        Assert.Equal(128000u, NssmServiceTranslation.throttle_milliseconds(99));
    }

    [Fact]
    public void service_environment_round_trips()
    {
        var name = "NSSM_MANAGER_TEST_" + Guid.NewGuid().ToString("N");
        var original = Environment.GetEnvironmentVariable(name);
        var service = new NssmServiceData
        {
            InitialEnvironment = NssmEnvironment.copy_environment(),
            ExtraEnvironment = NssmDoubleNull.FromStrings([$"{name}=expanded"])
        };
        try
        {
            NssmServiceTranslation.set_service_environment(service);
            Assert.Equal("expanded", Environment.GetEnvironmentVariable(name));
            NssmServiceTranslation.unset_service_environment(service);
            Assert.Equal(original, Environment.GetEnvironmentVariable(name));
        }
        finally { Environment.SetEnvironmentVariable(name, original); }
    }

    [Fact]
    public void service_manager_helpers_reject_invalid_handles()
    {
        Assert.Equal(IntPtr.Zero, NssmServiceTranslation.open_service(IntPtr.Zero, "missing", 1, out var canonical, 256));
        Assert.Null(canonical);
        Assert.Null(NssmServiceTranslation.query_service_config("missing", IntPtr.Zero));
        Assert.Equal(-1, NssmServiceTranslation.set_service_dependencies("missing", IntPtr.Zero, null));
        Assert.Equal(1, NssmServiceTranslation.set_service_description("missing", IntPtr.Zero, "x"));
        Assert.Equal(4, NssmServiceTranslation.get_service_description("missing", IntPtr.Zero, 256, out var description));
        Assert.Empty(description);
    }

    [Fact]
    public void dependency_helpers_match_upstream()
    {
        Assert.Equal(0, NssmServiceTranslation.prepend_service_group_identifier("NetworkProvider", out var group));
        Assert.Equal("+NetworkProvider", group);
        Assert.Equal(0, NssmServiceTranslation.prepend_service_group_identifier("+NetworkProvider", out group));
        Assert.Equal("+NetworkProvider", group);

        var initial = NssmDoubleNull.FromStrings(["RpcSs"]);
        Assert.Equal(0, NssmServiceTranslation.append_to_dependencies(initial, (uint)initial.Length, "NetworkProvider", out var appended, out var length, NssmServiceTranslation.DependencyGroups));
        Assert.Equal(new[] { "RpcSs", "+NetworkProvider" }, NssmDoubleNull.ToStrings(appended, length));
        Assert.Equal(0, NssmServiceTranslation.remove_from_dependencies(appended, length, "networkprovider", out var removed, out var removedLength, NssmServiceTranslation.DependencyGroups));
        Assert.Equal(new[] { "RpcSs" }, NssmDoubleNull.ToStrings(removed, removedLength));
        Assert.Equal(3, NssmServiceTranslation.get_service_dependencies("missing", IntPtr.Zero, out _, out _, NssmServiceTranslation.DependencyAll));
        Assert.Equal(3, NssmServiceTranslation.get_service_dependencies("missing", IntPtr.Zero, out _, out _));
    }

    [Fact]
    public void startup_and_username_match_upstream()
    {
        var manual = Config(3, "LocalSystem");
        Assert.Equal(0, NssmServiceTranslation.get_service_startup("x", IntPtr.Zero, manual, out var startup));
        Assert.Equal(NssmServiceTranslation.NssmStartupManual, startup);
        Assert.Equal(1, NssmServiceTranslation.get_service_startup("x", IntPtr.Zero, null, out _));
        Assert.Equal(0, NssmServiceTranslation.get_service_username("x", manual, out var username, out var length));
        Assert.Null(username);
        Assert.Equal((nuint)0, length);

        var account = Config(2, @"DOMAIN\user");
        Assert.Equal(0, NssmServiceTranslation.get_service_username("x", account, out username, out length));
        Assert.Equal(@"DOMAIN\user", username);
        Assert.Equal((nuint)11, length);
        Assert.Equal(1, NssmServiceTranslation.get_service_username("x", null, out _, out _));
    }

    [Fact]
    public void defaults_and_cleanup_match_upstream()
    {
        var service = NssmServiceTranslation.alloc_nssm_service();
        Assert.NotNull(service);
        NssmServiceTranslation.set_nssm_service_defaults(service);
        Assert.Equal(0x10u, service!.Type);
        Assert.Equal(NssmServiceTranslation.NormalPriorityClass, service.Priority);
        Assert.Equal(1500u, service.ThrottleDelay);
        Assert.Equal(uint.MaxValue, service.StopMethod);
        Assert.True(service.KillProcessTree);
        var password = "secret".ToCharArray();
        service.Password = password;
        NssmServiceTranslation.cleanup_nssm_service(service);
        Assert.True(service.Disposed);
        Assert.All(password, character => Assert.Equal('\0', character));
        NssmServiceTranslation.cleanup_nssm_service(service);
    }

    [Fact]
    public void manager_operations_validate_arguments()
    {
        WindowsServiceManager.set_service_recovery("missing", IntPtr.Zero);
        var manager = new WindowsServiceManager();
        Assert.ThrowsAny<Exception>(() => manager.install_service(new NssmManager.Contracts.NssmServiceConfiguration
        {
            Name = "bad/name",
            Application = Environment.ProcessPath ?? "missing"
        }, Environment.ProcessPath ?? "missing"));
    }

    [Fact]
    public void dispatcher_text_and_control_matrix_match_upstream()
    {
        Assert.Equal("START", WindowsServiceDispatcher.service_control_text(0));
        Assert.Equal("STOP", WindowsServiceDispatcher.service_control_text(1));
        Assert.Equal("POWEREVENT", WindowsServiceDispatcher.service_control_text(13));
        Assert.Equal("ROTATE", WindowsServiceDispatcher.service_control_text(128));
        Assert.Null(WindowsServiceDispatcher.service_control_text(127));
        Assert.Equal("SERVICE_STOPPED", WindowsServiceDispatcher.service_status_text(1));
        Assert.Equal("SERVICE_PAUSED", WindowsServiceDispatcher.service_status_text(7));
        Assert.Null(WindowsServiceDispatcher.service_status_text(0));
    }

    private static NssmQueryServiceConfig Config(uint startType, string account) =>
        new(0x10, startType, 1, "image", string.Empty, 0, [], account, "display");
}
