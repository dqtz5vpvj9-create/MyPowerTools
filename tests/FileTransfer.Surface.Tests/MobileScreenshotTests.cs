using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace FileTransfer.Surface.Tests;

/// <summary>
/// Renders the states the reviewer asked to see, so the conversation can be judged as a picture and
/// not only from a passing command. Frames are written only when <c>MPT_MOBILE_SCREENSHOTS</c> points
/// at a directory. Each scene is a real module answer or a real composer state, so a screenshot cannot
/// show something the module could not produce.
/// </summary>
public sealed class MobileScreenshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mpt-ft-shot-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTransferModule _module = new();
    private readonly List<Window> _windows = [];

    public MobileScreenshotTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var window in _windows) window.Close();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    public static TheoryData<string, int, string> Scenes => new()
    {
        // The four states the consolidated review asked to see.
        { "self-empty", 320, "light" },
        { "self-with-content", 390, "dark" },
        { "device-picker", 390, "light" },
        { "link-qr", 390, "light" }
    };

    [AvaloniaTheory]
    [MemberData(nameof(Scenes))]
    public async Task Capture(string scene, int width, string theme)
    {
        var directory = Environment.GetEnvironmentVariable("MPT_MOBILE_SCREENSHOTS");
        if (string.IsNullOrEmpty(directory)) return;

        var file = Path.Combine(_root, "周末出游计划.pdf");
        await File.WriteAllTextAsync(file, new string('x', 1024 * 128));
        var image = Path.Combine(_root, "山间的早晨.jpg");
        await File.WriteAllTextAsync(image, new string('y', 1024 * 64));

        Configure(scene);
        var view = new TransferView(_module.Context(_root, theme));
        var window = new Window { Width = width, Height = 844, Content = view };
        window.Show();
        _windows.Add(window);
        for (var pass = 0; pass < 4; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        await TestPump.RunAsync(() => view.Assistant.RefreshAsync());
        window.UpdateLayout();

        switch (scene)
        {
            case "self-with-content":
                // A real share fills the composer; the sent entries come from the module's session.
                await view.ActivateAsync(new global::MyPowerTools.Abstractions.ToolActivationRequest("file-transfer", "workspace", new Uri(image).AbsoluteUri));
                view.Conversation.AddAttachment(file);
                window.UpdateLayout();
                Save(window, directory, $"{scene}-{width}-{theme}");
                break;
            case "link-qr":
                // The connection sheet after the user asked for the code: a 240 px symbol plus the
                // separate copy action, so the sheet's real height and the symbol are visible.
                view.Conversation.OpenLinkSheet();
                await TestPump.RunAsync(() => view.Conversation.ExportLinkForTestAsync());
                window.UpdateLayout();
                Save(window, directory, $"{scene}-{width}-{theme}");
                break;
            case "device-picker":
                // The picker is what this scene shows, so it is opened directly instead of by
                // sending: the send would leave the conversation, not the picker, on screen.
                view.Conversation.AddAttachment(file);
                view.Conversation.OpenDevicePicker();
                window.UpdateLayout();
                Save(window, directory, $"{scene}-{width}-{theme}");
                break;
            default:
                Save(window, directory, $"{scene}-{width}-{theme}");
                break;
        }
    }

    /// <summary>Each scene is a session the module reports, or a real composer state.</summary>
    private void Configure(string scene)
    {
        _module.AssistantIdentityName = "我的手机";
        switch (scene)
        {
            case "self-empty":
                _module.AssistantRelayState = "available";
                break;
            case "self-with-content":
                _module.AssistantRelayState = "available";
                _module.AssistantLinked = true;
                _module.AssistantItems.Add(Item("item-1", "text", null, 0, "delivered", "买牛奶", "我的电脑"));
                _module.AssistantItems.Add(Item("item-2", "file", "产品草图.fig", 8640000, "delivered", null, "工作电脑"));
                _module.AssistantItems.Add(Item("item-3", "image", "山间的早晨.jpg", 51200, "stored"));
                break;
            case "device-picker":
            case "link-qr":
                _module.AssistantRelayState = "available";
                _module.AssistantDevices.Add(Device("pc-1", "工作电脑", "windows", paired: false, available: true));
                _module.AssistantDevices.Add(Device("mac-2", "MacBook Pro", "macos", paired: true, available: true));
                _module.AssistantDevices.Add(Device("old-3", "书房台式机", "windows", paired: true, available: false));
                break;
            case "receive-request":
                _module.AssistantRelayState = "available";
                _module.AssistantRequests.Add(new JsonObject
                {
                    ["requestId"] = "req-1",
                    ["deviceId"] = "pc-1",
                    ["name"] = "工作电脑",
                    ["itemNames"] = new JsonArray("季度报告.pdf", "照片.png"),
                    ["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(2).ToString("O")
                });
                break;
            case "offline-failed":
                // No relay configured and one entry the module reported as failed: the page must show
                // local-only saving plus an in-place retry rather than a fake success.
                _module.AssistantRelayState = "unconfigured";
                _module.AssistantItems.Add(Item("item-5", "text", null, 0, "queued", "断网时写的"));
                _module.AssistantItems.Add(Item("item-6", "file", "大文件.zip", 18600000, "failed", null, null, "网络中断。"));
                break;
        }
    }

    private static JsonObject Item(string id, string kind, string? name, long size, string state,
        string? text = null, string? receiptDevice = null, string? error = null)
    {
        var item = new JsonObject
        {
            ["id"] = id,
            ["kind"] = kind,
            ["text"] = text,
            ["name"] = name,
            ["size"] = size,
            ["createdAt"] = DateTimeOffset.UtcNow.AddMinutes(-3).ToString("O"),
            ["senderDeviceId"] = "mpt-phone",
            ["senderName"] = "我的手机",
            ["state"] = state,
            ["bytesDone"] = state == "failed" ? size / 4 : size,
            ["error"] = error,
            ["receipts"] = new JsonArray()
        };
        if (receiptDevice is { Length: > 0 })
            item["receipts"] = new JsonArray(new JsonObject
            {
                ["deviceId"] = "pc-1",
                ["deviceName"] = receiptDevice,
                ["at"] = DateTimeOffset.UtcNow.ToString("O")
            });
        return item;
    }

    private static JsonObject Device(string id, string name, string platform, bool paired, bool available) => new()
    {
        ["deviceId"] = id,
        ["name"] = name,
        ["address"] = "100.64.0.5",
        ["platform"] = platform,
        ["paired"] = paired,
        ["available"] = available
    };

    private static Button FindButton(Control view, string content) =>
        view.GetLogicalDescendants().OfType<Button>().First(button => (button.Content as string) == content);

    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        if (center is null) return;
        window.MouseDown(center.Value, MouseButton.Left);
        window.MouseUp(center.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string directory, string name)
    {
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(directory, name + ".png"));
    }
}
