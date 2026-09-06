using MyPowerTools.ServiceManager.Server;

namespace MyPowerTools.Tests;

public sealed class ServiceUnitCatalogTests
{
    [Fact]
    public void Readiness_address_expands_macos_temporary_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-service-catalog-tests", Guid.NewGuid().ToString("N"));
        var units = Path.Combine(root, "units");
        Directory.CreateDirectory(units);
        try
        {
            File.WriteAllText(Path.Combine(units, "notifications.json"), """
                {
                  "id": "remote-notifications.service",
                  "toolId": "remote-notifications",
                  "displayName": "Remote Notifications Service",
                  "exec": "RemoteNotifications.Service",
                  "readiness": {
                    "kind": "unix-socket",
                    "address": "$TMPDIR/mypowertools/remote-notifications.core.sock",
                    "timeoutMs": 8000
                  }
                }
                """);

            var catalog = new ServiceUnitCatalog(root);

            Assert.Equal(1, catalog.Reload());
            var manifest = catalog.TryGet("remote-notifications.service");
            Assert.NotNull(manifest);
            var readiness = manifest.EffectiveReadiness;
            Assert.Equal(
                Path.Combine(Path.GetTempPath(), "mypowertools", "remote-notifications.core.sock"),
                readiness.Address);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
