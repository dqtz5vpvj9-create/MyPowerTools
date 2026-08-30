using System.Text.Json;
using NssmManager.Contracts;

namespace NssmManager.Tests;

public sealed class RuntimeContractTests
{
    [Fact]
    public void Migration_snapshot_has_explicit_schema_and_timestamp()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new NssmMigrationSnapshot(1, "svc", @"C:\nssm.exe", new NssmServiceConfiguration { Name = "svc", Application = @"C:\app.exe" }, now, NssmServiceState.Running);
        var json = JsonSerializer.Serialize(snapshot);
        var roundTrip = JsonSerializer.Deserialize<NssmMigrationSnapshot>(json);
        Assert.NotNull(roundTrip);
        Assert.Equal(1, roundTrip.SchemaVersion);
        Assert.Equal("svc", roundTrip.ServiceName);
        Assert.Equal(now, roundTrip.CreatedAt);
        Assert.Equal(NssmServiceState.Running, roundTrip.State);
    }

    [Fact]
    public void Settings_lookup_is_case_insensitive()
    {
        Assert.Equal("AppRotateOnline", NssmSettings.Find("approtateonline").Name);
        Assert.Throws<ArgumentException>(() => NssmSettings.Find("AppUnknown"));
    }

    [Fact]
    public void Native_settings_are_marked_for_SCM_routing()
    {
        Assert.All(NssmSettings.All.Where(item => item.Name is "DisplayName" or "ObjectName" or "Start" or "Type"), item => Assert.True(item.Native));
        Assert.False(NssmSettings.Find("Application").Native);
    }
}
