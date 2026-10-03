using System.Text.Json;
using LocalLagCleaner.MyPowerTools;

namespace MyPowerTools.Tests;

public sealed class LocalLagCleanerElevationPreflightTests
{
    [Theory]
    [InlineData(CleanupAction.DeliveryOptimization)]
    [InlineData(CleanupAction.NvidiaContainer)]
    [InlineData(CleanupAction.WindowsSearch)]
    [InlineData(CleanupAction.RemoteDesktop)]
    public void Service_confirmation_is_validated_without_consuming_or_executing_the_plan(CleanupAction action)
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-lag-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var plan = new CleanupPlan(Guid.NewGuid().ToString("N"), "A1B2C3D4", action,
                now, now.AddMinutes(10), [], "isolated test", "isolated test", true,
                action == CleanupAction.RemoteDesktop);
            var path = Path.Combine(root, "pending-cleanup.json");
            File.WriteAllText(path, JsonSerializer.Serialize(plan, LagCleanerJson.Compact));
            var cleaner = new CleanupCoordinator(root);
            Assert.Throws<UnauthorizedAccessException>(() => cleaner.ValidatePendingPlan(
                plan.PlanId, action, "B1B2C3D4", true, true));
            Assert.Throws<InvalidOperationException>(() => cleaner.ValidatePendingPlan(
                Guid.NewGuid().ToString("N"), action, plan.ConfirmationToken, true, true));
            Assert.Throws<InvalidOperationException>(() => cleaner.ValidatePendingPlan(
                plan.PlanId, action == CleanupAction.WindowsSearch ? CleanupAction.DeliveryOptimization : CleanupAction.WindowsSearch,
                plan.ConfirmationToken, true, true));
            Assert.Throws<InvalidOperationException>(() => cleaner.ValidatePendingPlan(
                plan.PlanId, action, plan.ConfirmationToken, true, false));
            if (action == CleanupAction.RemoteDesktop)
                Assert.Throws<InvalidOperationException>(() => cleaner.ValidatePendingPlan(
                    plan.PlanId, action, plan.ConfirmationToken, false, true));
            Assert.Equal(plan.PlanId, cleaner.ValidatePendingPlan(plan.PlanId, action, plan.ConfirmationToken, true, true).PlanId);
            Assert.Equal(plan.PlanId, cleaner.TryReadPendingPlan()!.PlanId);
            Assert.Empty(Directory.EnumerateFiles(root, "claimed-cleanup-*.json"));
            File.WriteAllText(path, JsonSerializer.Serialize(plan with { CreatedAtUtc = now.AddMinutes(-20), ExpiresAtUtc = now.AddMinutes(-10) }, LagCleanerJson.Compact));
            Assert.Throws<InvalidOperationException>(() => cleaner.ValidatePendingPlan(
                plan.PlanId, action, plan.ConfirmationToken, true, true));
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
