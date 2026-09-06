using System.Reflection;
using Avalonia.Headless.XUnit;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Protocol.HostControl.V1;

namespace PersonalUx.Tests;

public sealed class SurfaceThreadOwnershipTests
{
    [AvaloniaFact]
    public async Task Background_surface_load_is_rejected_before_creating_any_controls()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-surface-thread-" + Guid.NewGuid().ToString("N"));
        try
        {
            var type = typeof(ShellToolProductService).Assembly.GetType("MyPowerTools.Shell.Avalonia.Services.DotnetSurfaceLoader", true)!;
            var loader = Activator.CreateInstance(type, root)!;
            var method = type.GetMethod("Load")!;
            var exception = await Task.Run(() => Record.Exception(() => method.Invoke(loader, [new ToolDescriptor(), new ToolRoute(), null])));
            var invocation = Assert.IsType<TargetInvocationException>(exception);
            Assert.IsType<InvalidOperationException>(invocation.InnerException);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            // On the UI thread the same input reaches normal route validation.
            var uiError = Assert.Throws<TargetInvocationException>(() => method.Invoke(loader, [new ToolDescriptor(), new ToolRoute(), null]));
            Assert.IsType<FileNotFoundException>(uiError.InnerException);
        }
        finally { Directory.Delete(root, true); }
    }
}
