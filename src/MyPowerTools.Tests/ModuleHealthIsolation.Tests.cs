using MyPowerTools.Abstractions;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using ModuleContext = MyPowerTools.Abstractions.ModuleContext;
using ModuleStatusSnapshot = MyPowerTools.Abstractions.ModuleStatusSnapshot;
using SettingsSchemaDocument = MyPowerTools.Abstractions.SettingsSchemaDocument;
using MptCommandDescriptor = MyPowerTools.Abstractions.MptCommandDescriptor;
using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using CommandExecutionResult = MyPowerTools.Abstractions.CommandExecutionResult;

namespace MyPowerTools.Tests;

public sealed class ModuleHealthIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Module_internal_cancellation_preserves_dashboard_and_catalog(bool refreshCommands)
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-health-isolation-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current(), RuntimePaths.Create(root), [new CancelledStatusRuntime()]);
            runtime.Load(Path.Combine(FindRoot(), "modules"));
            if (refreshCommands) await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
            else await runtime.RefreshHealthAsync(CancellationToken.None);
            var dashboard = runtime.GetDashboardSnapshot();
            Assert.NotEmpty(dashboard.Cards);
            Assert.Contains(dashboard.Cards, card => card.State == "degraded" && card.Summary.Contains("module budget cancelled"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Caller_cancellation_still_stops_health_refresh()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-health-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current(), RuntimePaths.Create(root), [new CancelledStatusRuntime()]);
            runtime.Load(Path.Combine(FindRoot(), "modules"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.RefreshHealthAsync(cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.RefreshDynamicCommandsAsync(cancellation.Token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root missing.");
    }

    private sealed class CancelledStatusRuntime : IModuleTransportRuntime
    {
        public string Kind => "inproc-dotnet";
        public ValueTask<ModuleStatusSnapshot?> GetStatusAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
            => ValueTask.FromException<ModuleStatusSnapshot?>(new OperationCanceledException("module budget cancelled", new CancellationToken(true)));
        public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new SettingsSchemaDocument(module.Module.Manifest.Id, "{}"));
        public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<MptCommandDescriptor>>([]);
        public ValueTask<CommandExecutionResult> ExecuteCommandAsync(RuntimeModuleRecord module, ModuleContext context, CommandRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Health tests must never execute a command.");
    }
}
