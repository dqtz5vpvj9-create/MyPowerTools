using System.Text.Json.Nodes;

namespace MyPowerTools.Tests;

public sealed class PasteImageProductTests
{
    [Fact]
    public void Paste_image_declares_a_dotnet_surface()
    {
        var tool = JsonNode.Parse(File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "paste-image",
            "current-integration",
            "modules",
            "paste-image",
            "ui",
            "tool.json")))!.AsObject();

        Assert.Equal("dotnet-surface", tool["type"]?.GetValue<string>());
        var surface = tool["routes"]?[0]?["surface"];
        Assert.Equal("dotnet", surface?["kind"]?.GetValue<string>());
        Assert.Equal("surface/PasteImage.Surface.dll", surface?["assembly"]?.GetValue<string>());
        Assert.Equal("PasteImage.Surface.PasteImageSurfaceFactory", surface?["type"]?.GetValue<string>());
    }

    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Paste_image_manifest_replaces_the_ahk_hotkey_with_a_host_binding()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "modules", "paste-image", "module.json")))!.AsObject();
        var hotkey = Assert.IsType<JsonObject>(Assert.Single(manifest["hotkeys"]!.AsArray()));

        Assert.Equal("Ctrl+Alt+V", hotkey["default"]!.GetValue<string>());
        Assert.Equal("paste-image.upload", hotkey["commandId"]!.GetValue<string>());
        Assert.True(hotkey["enabledByDefault"]!.GetValue<bool>());
        Assert.Equal("inproc-dotnet", manifest["entrypoints"]![0]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Paste_image_uses_platform_clipboards_and_structured_openssh_arguments()
    {
        var source = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "paste-image",
            "current-integration",
            "src",
            "PasteImage.MyPowerTools",
            "PasteImageModule.cs"));
        var windowsClipboard = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Platform.Windows",
            "WindowsClipboardImageService.cs"));
        var macClipboard = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Platform.Mac",
            "MacPasteboardImageService.cs"));
        var macNative = File.ReadAllText(Path.Combine(
            Root,
            "native",
            "macos",
            "MptMacNative",
            "MptMacNative.mm"));

        Assert.Contains("TryGetCapability<IClipboardImageService>(\"clipboard.image\"", source, StringComparison.Ordinal);
        Assert.Contains("clipboard.ReadPngAsync", source, StringComparison.Ordinal);
        Assert.Contains("clipboard.WriteTextAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DllImport", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Drawing", source, StringComparison.Ordinal);
        Assert.Contains("GetClipboardData(NativeMethods.CfBitmap)", windowsClipboard, StringComparison.Ordinal);
        Assert.Contains("SetClipboardData(NativeMethods.CfUnicodeText", windowsClipboard, StringComparison.Ordinal);
        Assert.Contains("Image.FromHbitmap", windowsClipboard, StringComparison.Ordinal);
        Assert.Contains("MacNative.ReadPasteboardPng", macClipboard, StringComparison.Ordinal);
        Assert.Contains("NSPasteboard.generalPasteboard", macNative, StringComparison.Ordinal);
        Assert.Contains("mpt_pasteboard_read_png", macNative, StringComparison.Ordinal);
        Assert.Contains("mpt_pasteboard_write_text", macNative, StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(argument)", source, StringComparison.Ordinal);
        Assert.Contains("BatchMode=yes", source, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardInput = true", source, StringComparison.Ordinal);
        Assert.Contains("mkdir -p -- {remoteDirectory} && cat > {remotePath}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("scp.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AutoHotkey", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Expression", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("notification.desktop", source, StringComparison.Ordinal);
        Assert.Contains("paste-image.notification.test", source, StringComparison.Ordinal);
        Assert.Contains("Paste Image 上传成功", source, StringComparison.Ordinal);
        Assert.Contains("Paste Image 上传失败", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Paste_image_surface_uses_mvvm_and_runner_event_push()
    {
        var surfaceRoot = Path.Combine(
            Root,
            "tools",
            "paste-image",
            "current-integration",
            "src",
            "PasteImage.Surface");
        var viewModel = File.ReadAllText(Path.Combine(surfaceRoot, "ViewModels", "PasteImageViewModel.cs"));
        var view = File.ReadAllText(Path.Combine(surfaceRoot, "Views", "PasteImageView.axaml"));
        var sdk = File.ReadAllText(Path.Combine(Root, "src", "MyPowerTools.AvaloniaSdk", "SurfaceContracts.cs"));
        var shell = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Services",
            "ShellWorkspaceController.ExternalTools.cs"));

        Assert.Contains("ObservableCollection<UploadHistoryRow>", viewModel, StringComparison.Ordinal);
        Assert.Contains("context.SubscribeEvents?.Invoke(OnSurfaceEvent)", viewModel, StringComparison.Ordinal);
        Assert.Contains("upload.alert", viewModel, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding History}\"", view, StringComparison.Ordinal);
        Assert.Contains("Func<Action<MptSurfaceEvent>, IDisposable>? SubscribeEvents", sdk, StringComparison.Ordinal);
        Assert.Contains("SubscribeSurfaceEvents(descriptor.OwnerModuleId", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Paste_image_builds_as_a_platform_neutral_module()
    {
        var project = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "paste-image",
            "current-integration",
            "src",
            "PasteImage.MyPowerTools",
            "PasteImage.MyPowerTools.csproj"));
        var build = File.ReadAllText(Path.Combine(Root, "tools", "paste-image", "build.ps1"));

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", project, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Drawing.Common", project, StringComparison.Ordinal);
        Assert.Contains("StageRepositoryModule", project, StringComparison.Ordinal);
        Assert.Contains("PasteImage.MyPowerTools.deps.json", build, StringComparison.Ordinal);
        Assert.Contains("staleRuntimeFile", build, StringComparison.Ordinal);
    }

    [Fact]
    public void Paste_image_defaults_match_the_supplied_launcher_configuration()
    {
        var source = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "paste-image",
            "current-integration",
            "src",
            "PasteImage.MyPowerTools",
            "PasteImageModule.cs"));

        Assert.Contains("[\"remoteHost\"] = \"chris\"", source, StringComparison.Ordinal);
        Assert.Contains("[\"remoteDirectory\"] = \"/tmp\"", source, StringComparison.Ordinal);
        Assert.Contains("[\"uploadTimeoutSeconds\"] = 30", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the MyPowerTools repository root.");
    }
}
