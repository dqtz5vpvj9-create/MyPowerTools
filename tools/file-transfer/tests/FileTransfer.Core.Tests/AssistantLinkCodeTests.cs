using System.Text;
using System.Text.Json.Nodes;
using FileTransfer.Core;
using FileTransfer.MyPowerTools;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace FileTransfer.Tests;

/// <summary>
/// External connection codes are untrusted input. A synthetic Android deeplink that carries only
/// version/conversationId/deviceName/token must be rejected with a clear invalid-code answer instead
/// of a NullReferenceException, a legacy pair code must say which command handles it, and a real
/// export must preview successfully without ever returning the conversation key.
/// </summary>
public sealed class AssistantLinkCodeTests : IAsyncDisposable
{
    private const string MalformedAndroidDeeplink =
        "mpt://assistant/eyJ2ZXJzaW9uIjoxLCJjb252ZXJzYXRpb25JZCI6InNlbGYtdGVzdC1jb252IiwiZGV2aWNlTmFtZSI6Iua1i-ivleeUteiEkSIsInRva2VuIjoiczNjcmV0LWdyYW50LXRva2VuLTAxMjM0NTY3ODlhYmNkZWYifQ";

    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(),
        "mpt-link-code-" + Guid.NewGuid().ToString("N"));

    public AssistantLinkCodeTests() => Directory.CreateDirectory(_root);

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private async Task<FileTransferModule> StartAsync(string deviceId, string address)
    {
        var secrets = new InMemorySecretStore();
        var preferences = new JsonObject
        {
            ["deviceId"] = deviceId,
            ["listenAddress"] = address,
            ["receiveDirectory"] = Path.Combine(_root, "inbox"),
            ["peers"] = new JsonArray()
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "preferences.json"), preferences.ToJsonString());
        var context = new ModuleContext("test", "1.0", "file-transfer", "file-transfer", _root, _root, _root, "linux",
            ["secret.store"], new Dictionary<string, object> { ["secret.store"] = secrets });
        var module = new FileTransferModule();
        Assert.True((await module.InitializeAsync(context, CancellationToken.None)).Ok);
        return module;
    }

    private static async Task<JsonObject> CallAsync(FileTransferModule module, string command, JsonObject? args = null)
    {
        var result = await module.ExecuteCommandAsync(new CommandRequest(Guid.NewGuid().ToString("N"), command, args ?? new JsonObject()), CancellationToken.None);
        Assert.True(result.Success, result.Output);
        return JsonNode.Parse(result.Output)!.AsObject();
    }

    private static async Task<CommandExecutionResult> TryAsync(FileTransferModule module, string command, JsonObject args) =>
        await module.ExecuteCommandAsync(new CommandRequest(Guid.NewGuid().ToString("N"), command, args), CancellationToken.None);

    // ---- the reported Android failure ---------------------------------------------------------

    [Fact]
    public void TheMalformedAndroidDeeplinkIsRejectedWithAClearMessage()
    {
        var error = Assert.Throws<ArgumentException>(() => LinkCode.Decode(MalformedAndroidDeeplink));
        Assert.Contains("连接码", error.Message);
        Assert.DoesNotContain("Object reference", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("s3cret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewingTheMalformedDeeplinkFailsWithTheInvalidCodeMessage()
    {
        var module = await StartAsync("phone-a", "127.0.0.81");
        var result = await TryAsync(module, "file-transfer.assistant.link.preview",
            new JsonObject { ["code"] = MalformedAndroidDeeplink });
        Assert.False(result.Success);
        Assert.Contains("连接码", result.Output);
        Assert.DoesNotContain("Object reference", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NullReference", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("deviceId")]
    [InlineData("key")]
    [InlineData("conversationId")]
    public void AMissingRequiredFieldNamesTheProblemInsteadOfThrowingNull(string missing)
    {
        var code = LinkCode.Encode(new LinkCode.Payload(1, "self-abc12345", new string('a', 64),
            "phone-local", "My Phone", "100.64.0.9", TransferFiles.Port, "android"));
        var withoutField = RemoveField(code, missing);
        var error = Assert.Throws<ArgumentException>(() => LinkCode.Decode(withoutField));
        Assert.Contains("连接码", error.Message);
    }

    [Fact]
    public void AMissingDisplayNameStillDecodesBecauseItIsCosmetic()
    {
        var code = LinkCode.Encode(new LinkCode.Payload(1, "self-abc12345", new string('a', 64),
            "phone-local", "My Phone", "100.64.0.9", TransferFiles.Port, "android"));
        var decoded = LinkCode.Decode(RemoveField(code, "name"));
        Assert.Equal("phone-local", decoded.DeviceId);
        Assert.Equal("", decoded.Name);
    }

    [Fact]
    public void NullEmptyAndUnrelatedInputIsRejectedAsAnInvalidCode()
    {
        Assert.Throws<ArgumentException>(() => LinkCode.Decode(""));
        Assert.Throws<ArgumentException>(() => LinkCode.Decode("   "));
        Assert.Throws<ArgumentException>(() => LinkCode.Decode("123456"));
        Assert.Throws<ArgumentException>(() => LinkCode.Decode("mpt://assistant/"));
        Assert.Throws<ArgumentException>(() => LinkCode.Decode("mpt://assistant/%%%not-base64%%%"));
        Assert.Throws<ArgumentException>(() => LinkCode.Decode("mpt://assistant/" + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"version\":2}"))));
    }

    [Fact]
    public void ALegacyPairOrCloudCodeNamesTheSettingsPageThatHandlesIt()
    {
        var pair = new Pairing("pc-peer", "书房电脑", "100.64.0.9", "pairing-token-0123456789abcdef").Encode();
        var pairError = Assert.Throws<ArgumentException>(() => LinkCode.Decode(pair));
        Assert.Contains("添加设备", pairError.Message);

        var cloud = new CloudConnection("https://relay.example/dav", "mpt-relay", "relay-password").Encode();
        var cloudError = Assert.Throws<ArgumentException>(() => LinkCode.Decode(cloud));
        Assert.Contains("网盘中转", cloudError.Message);
    }

    // ---- real export -> preview ---------------------------------------------------------------

    [Fact]
    public async Task ARealExportPreviewsWithoutReturningTheKey()
    {
        var module = await StartAsync("phone-a", "100.64.0.9");
        var export = await CallAsync(module, "file-transfer.assistant.link.export");
        var code = export["code"]!.GetValue<string>();
        Assert.StartsWith(LinkCode.Prefix, code);

        var preview = await CallAsync(module, "file-transfer.assistant.link.preview", new JsonObject { ["code"] = code });
        Assert.Equal("phone-a", preview["deviceId"]!.GetValue<string>());
        Assert.Equal("own-devices", preview["scope"]!.GetValue<string>());
        Assert.StartsWith("self-", preview["conversationId"]!.GetValue<string>());
        // The key and any secret must never travel in a preview answer.
        Assert.DoesNotContain("key", preview.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        var payload = LinkCode.Decode(code);
        Assert.Equal(64, payload.Key.Length);
        Assert.False(preview.ToJsonString().Contains(payload.Key, StringComparison.Ordinal));
    }

    [Fact]
    public void EncodeAndDecodeKeepEveryFieldAndStillReadTheEarlierPrefix()
    {
        var payload = new LinkCode.Payload(1, "self-abc12345", new string('b', 64), "phone-local", "My Phone",
            "100.64.0.9", TransferFiles.Port, "android",
            new CloudConnection("https://relay.example/dav", "mpt-relay", "relay-password").Encode());
        var code = LinkCode.Encode(payload);
        var decoded = LinkCode.Decode(code);
        Assert.Equal(payload.ConversationId, decoded.ConversationId);
        Assert.Equal(payload.DeviceId, decoded.DeviceId);
        Assert.Equal(payload.Name, decoded.Name);
        Assert.Equal(payload.Address, decoded.Address);
        Assert.Equal(payload.Platform, decoded.Platform);
        Assert.Equal(payload.Cloud, decoded.Cloud);

        // The earlier build's prefix is still readable as input only.
        var legacy = "mpt://link/" + code[LinkCode.Prefix.Length..];
        Assert.Equal(payload.ConversationId, LinkCode.Decode(legacy).ConversationId);
        Assert.DoesNotContain("mpt://link/", LinkCode.Encode(payload));
    }

    // ---- existing pair codes ------------------------------------------------------------------

    [Fact]
    public async Task AnExistingPairCodeCanBePreviewedAndImported()
    {
        var module = await StartAsync("phone-a", "100.64.0.9");
        var token = "pairing-token-0123456789abcdef";
        var code = new Pairing("pc-peer", "书房电脑", "100.64.0.9", token).Encode();

        var preview = await CallAsync(module, "file-transfer.pair.preview", new JsonObject { ["code"] = code });
        Assert.Equal("pc-peer", preview["deviceId"]!.GetValue<string>());
        Assert.Equal("书房电脑", preview["name"]!.GetValue<string>());
        Assert.Equal("100.64.0.9", preview["address"]!.GetValue<string>());
        // A preview never returns the pairing secret.
        Assert.DoesNotContain(token, preview.ToJsonString());

        var imported = await CallAsync(module, "file-transfer.pair.import", new JsonObject { ["code"] = code });
        Assert.Equal("pc-peer", imported["deviceId"]!.GetValue<string>());
        var peers = (await CallAsync(module, "file-transfer.inspect"))["peers"]!.AsArray();
        Assert.Single(peers);
        Assert.Equal("pc-peer", peers[0]!["deviceId"]!.GetValue<string>());
    }

    [Fact]
    public async Task AMalformedPairCodeIsRejectedWithAClearMessage()
    {
        var module = await StartAsync("phone-a", "100.64.0.9");
        var result = await TryAsync(module, "file-transfer.pair.preview", new JsonObject { ["code"] = "mpt://pair/not-a-payload" });
        Assert.False(result.Success);
        Assert.Contains("连接码", result.Output);
        Assert.DoesNotContain("Object reference", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rebuilds a code without one JSON field, the way a malformed producer would.</summary>
    private static string RemoveField(string code, string field)
    {
        var base64 = code[LinkCode.Prefix.Length..].Replace('-', '+').Replace('_', '/');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')));
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove(field);
        return LinkCode.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(node.ToJsonString()))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
