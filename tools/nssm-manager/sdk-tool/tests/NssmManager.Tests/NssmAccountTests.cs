using System.Security.Principal;
using NssmManager.Windows;

namespace NssmManager.Tests;

public sealed class NssmAccountTests
{
    [Fact]
    public void open_lsa_policy_returns_upstream_status()
    {
        if (!OperatingSystem.IsWindows()) return;
        var status = NssmAccount.open_lsa_policy(out var policy);
        Assert.Contains(status, new[] { 0, 1 });
        policy?.Dispose();
    }

    [Fact]
    public void username_sid_matches_lsa_lookup_names()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (NssmAccount.open_lsa_policy(out var policy) != 0 || policy is null) return;
        using (policy)
        {
            Assert.Equal(0, NssmAccount.username_sid("LocalSystem", out var sid, policy));
            Assert.NotNull(sid);
            Assert.True(sid!.IsWellKnown(WellKnownSidType.LocalSystemSid));
        }
    }

    [Fact]
    public void canonicalise_username_matches_lsa_lookup_sids()
    {
        if (!OperatingSystem.IsWindows()) return;
        var status = NssmAccount.canonicalise_username("LocalSystem", out var canonical);
        if (status == 1) return;
        Assert.Equal(0, status);
        Assert.EndsWith(@"\SYSTEM", canonical, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void username_equiv_compares_sids()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (NssmAccount.open_lsa_policy(out var policy) != 0)
        {
            policy?.Dispose();
            return;
        }
        policy?.Dispose();
        Assert.Equal(1, NssmAccount.username_equiv("LocalSystem", @"NT AUTHORITY\SYSTEM"));
    }

    [Fact]
    public void is_localsystem_accepts_alias_and_sid_name()
    {
        Assert.Equal(1, NssmAccount.is_localsystem("localsystem"));
        if (OperatingSystem.IsWindows() && NssmAccount.open_lsa_policy(out var policy) == 0)
        {
            policy?.Dispose();
            Assert.Equal(1, NssmAccount.is_localsystem(@"NT AUTHORITY\SYSTEM"));
        }
    }

    [Fact]
    public void virtual_account_uses_nt_service_domain() =>
        Assert.Equal(@"NT Service\Example", NssmAccount.virtual_account("Example"));

    [Fact]
    public void is_virtual_account_is_case_insensitive()
    {
        Assert.Equal(1, NssmAccount.is_virtual_account("Example", @"nt service\example"));
        Assert.Equal(0, NssmAccount.is_virtual_account("Other", @"nt service\example"));
        Assert.Equal(0, NssmAccount.is_virtual_account(null, @"nt service\example"));
    }

    [Fact]
    public void well_known_sid_returns_nssm_aliases()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("LocalSystem", NssmAccount.well_known_sid(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.Equal(@"NT Authority\LocalService", NssmAccount.well_known_sid(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null)));
        Assert.Equal(@"NT Authority\NetworkService", NssmAccount.well_known_sid(new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null)));
    }

    [Fact]
    public void well_known_username_defaults_to_localsystem()
    {
        Assert.Equal("LocalSystem", NssmAccount.well_known_username(null));
        Assert.Equal("LocalSystem", NssmAccount.well_known_username("localsystem"));
    }

    [Fact]
    public void grant_logon_as_service_matches_lsa_right_enumeration() =>
        Assert.Equal(0, NssmAccount.grant_logon_as_service(null));
}
