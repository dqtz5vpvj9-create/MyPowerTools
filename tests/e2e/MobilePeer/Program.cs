using System.Text.Json.Nodes;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;
using FileTransfer.Core.Cloud;
using System.Text.Json;
using FileTransfer.MyPowerTools;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

// An isolated production module; stdin waits for test commands without polling.
var root = args[0];
Directory.CreateDirectory(root);
await File.WriteAllTextAsync(Path.Combine(root, "preferences.json"), new JsonObject
{
    ["deviceId"] = "mobile-e2e-peer",
    ["deviceName"] = "MPT E2E 电脑",
    ["listenAddress"] = "127.0.0.1",
    ["receiveDirectory"] = Path.Combine(root, "inbox")
}.ToJsonString());
var secrets = new InMemorySecretStore();
using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(30));
// Reuse a private QA namespace instead of leaving a new server namespace after every run.
// This file must belong to test devices; never supply a daily-use conversation invitation.
var invitationPath = Environment.GetEnvironmentVariable("MPT_E2E_INVITATION_FILE");
if (!string.IsNullOrEmpty(invitationPath) && File.Exists(invitationPath))
{
    var invitation = LinkCode.Decode(await File.ReadAllTextAsync(invitationPath, budget.Token));
    await secrets.SaveAsync("file-transfer", "conversation-id", invitation.ConversationId, budget.Token);
    await secrets.SaveAsync("file-transfer", "conversation-key", invitation.Key, budget.Token);
    await secrets.SaveAsync("file-transfer", "conversation-linked", "exported", budget.Token);
}
var module = new FileTransferModule();
var context = new ModuleContext("e2e", "1", "file-transfer", "file-transfer", root, root, root,
    "linux", ["secret.store"], new Dictionary<string, object> { ["secret.store"] = secrets });
if (!(await module.InitializeAsync(context, budget.Token)).Ok) throw new Exception("Peer initialization failed");
try
{
    while (await Console.In.ReadLineAsync(budget.Token) is { } line)
    {
        var request = JsonNode.Parse(line)!.AsObject();
        if (request["name"]!.GetValue<string>() == "e2e.share.publish")
        {
            // Private single-file capabilities enter through local fixture files, never action logs.
            var fixture = Environment.GetEnvironmentVariable("MPT_E2E_SHARE_DESCRIPTOR")!;
            var payload = Environment.GetEnvironmentVariable("MPT_E2E_SHARE_PAYLOAD")!;
            var share = JsonSerializer.Deserialize<QuarkShareDescriptor>(await File.ReadAllTextAsync(fixture), AssistantJson.Options)!;
            var conversation = (await secrets.ReadAsync(SecretReference.Create("file-transfer", "conversation-id"), budget.Token))!;
            var key = (await secrets.ReadAsync(SecretReference.Create("file-transfer", "conversation-key"), budget.Token))!;
            var message = new AssistantManifest { Id = Guid.NewGuid().ToString("N"), Kind = AssistantItemKind.File,
                Name = "MPT-E2E-quark-share.bin", Size = new FileInfo(payload).Length, CreatedAt = DateTimeOffset.UtcNow,
                SenderDeviceId = "mobile-e2e-peer", SenderName = "MPT E2E" };
            var offer = CloudAttachmentOffer.Create(conversation, message);
            using var relay = new CloudRelayClient(conversation, key);
            await relay.PublishAsync(offer, budget.Token, new(1, conversation, message, offer.ExpiresAt, share));
            Console.WriteLine(new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["id"] = message.Id } }.ToJsonString());
            continue;
        }
        var result = await module.ExecuteCommandAsync(new(Guid.NewGuid().ToString("N"),
            "file-transfer." + request["name"]!.GetValue<string>(), request["args"]?.AsObject() ?? new()), budget.Token);
        if (result.Success && request["name"]!.GetValue<string>() == "assistant.link.export"
            && !string.IsNullOrEmpty(invitationPath))
        {
            await using var file = new FileStream(invitationPath, new FileStreamOptions
            {
                Mode = FileMode.Create, Access = FileAccess.Write,
                UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            await using var writer = new StreamWriter(file);
            await writer.WriteAsync(JsonNode.Parse(result.Output)!["code"]!.GetValue<string>().AsMemory(), budget.Token);
        }
        // Only the test process reads this pipe; invitation credentials never enter reports.
        Console.WriteLine(new JsonObject { ["ok"] = result.Success,
            ["data"] = JsonNode.Parse(result.Output) }.ToJsonString());
    }
}
finally { await module.DisposeAsync(CancellationToken.None); }
