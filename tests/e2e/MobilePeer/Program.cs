using System.Text.Json.Nodes;
using FileTransfer.Core;
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
