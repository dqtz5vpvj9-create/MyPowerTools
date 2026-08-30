using NssmManager.Windows;

namespace NssmManager.Tests;

public sealed class NssmImportsTests
{
    [Fact]
    public void get_dll_reports_win32_loader_error()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(IntPtr.Zero, NssmImports.get_dll($"missing-{Guid.NewGuid():N}.dll", out var error));
        Assert.NotEqual(0u, error);
    }

    [Fact]
    public void get_import_reports_missing_export()
    {
        if (!OperatingSystem.IsWindows()) return;
        var module = NssmImports.get_dll("kernel32.dll", out var loadError);
        Assert.NotEqual(IntPtr.Zero, module);
        Assert.Equal(0u, loadError);
        Assert.Equal(IntPtr.Zero, NssmImports.get_import(module, $"missing_{Guid.NewGuid():N}", out var error));
        Assert.NotEqual(0u, error);
        NssmImports.free_imports();
    }

    [Fact]
    public void get_imports_matches_optional_import_contract()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(0, NssmImports.get_imports());
        Assert.NotEqual(IntPtr.Zero, NssmImports.AttachConsole);
    }

    [Fact]
    public void free_imports_zeros_every_slot()
    {
        NssmImports.free_imports();
        Assert.Equal(IntPtr.Zero, NssmImports.AttachConsole);
        Assert.Equal(IntPtr.Zero, NssmImports.QueryFullProcessImageName);
        Assert.Equal(IntPtr.Zero, NssmImports.IsWellKnownSid);
    }
}
