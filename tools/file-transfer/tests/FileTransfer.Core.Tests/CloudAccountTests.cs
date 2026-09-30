using System.Text.Json;
using System.Text.Json.Nodes;
using FileTransfer.Core;
using FileTransfer.Core.Cloud;
using FileTransfer.MyPowerTools;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace FileTransfer.Tests;

public sealed class CloudAccountTests
{
    [Fact]
    public void ProviderAdditionsUseRealDriverFields()
    {
        var quark = JsonSerializer.SerializeToNode(OpenListCloudAccountClient.Addition("quark", "test-cookie"))!;
        Assert.Equal("test-cookie", quark["cookie"]!.GetValue<string>());
        Assert.Equal("0", quark["root_folder_id"]!.GetValue<string>());
        var baidu = JsonSerializer.SerializeToNode(OpenListCloudAccountClient.Addition("baidu", "test-refresh"))!;
        Assert.True(baidu["use_online_api"]!.GetValue<bool>());
        Assert.Equal("https://api.oplist.org/baiduyun/renewapi", baidu["api_url_address"]!.GetValue<string>());
        Assert.Null(baidu["client_secret"]);
    }
    [Theory]
    [InlineData("/other")]
    [InlineData("/mount/../other")]
    [InlineData("/mount2")]
    [InlineData("/mount/\\outside")]
    public void DirectoryCannotEscapeMount(string path) => Assert.Throws<ArgumentException>(() => OpenListCloudAccountClient.ValidatePath("/mount", path));
    [Fact]
    public void DefaultRequiresReadyAndResumeDoesNotInventReadiness()
    {
        var account = new CloudAccount("a", "quark", "Q", "ready", "/mount/MPT", "MPT");
        var state = new CloudAccountState([account], new());
        Assert.Equal("a", CloudAccountRules.SetDefault(state, "a").Preferences.DefaultAccountId);
        var paused = CloudAccountRules.Pause(state, "a", true);
        Assert.Throws<ArgumentException>(() => CloudAccountRules.SetDefault(paused, "a"));
        Assert.Equal("blocked", CloudAccountRules.Pause(paused, "a", false).Accounts[0].Status);
        Assert.Throws<ArgumentException>(() => CloudAccountRules.SetMode(new(), "public"));
    }
    [Fact]
    public async Task MetadataRoundTripAndCorruptionDoesNotReset()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-cloud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "cloud.json"); var store = new CloudAccountStore(path);
            await store.SaveAsync(new([], new(Mode: "cloudOnly")), default);
            Assert.Equal("cloudOnly", (await store.LoadAsync(default)).Preferences.Mode);
            await File.WriteAllTextAsync(path, "invalid");
            await Assert.ThrowsAsync<JsonException>(() => store.LoadAsync(default));
            Assert.Equal("invalid", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task CancelledAuthorizationCannotCompleteAndCloudOnlyKeepsQueueAndSettings()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-cloud-module-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PublicRelayClient.BaseAddressOverride = () => new Uri("http://127.0.0.1:1/");
        var module = new FileTransferModule();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "preferences.json"), new JsonObject { ["deviceId"] = "cloud-test", ["listenAddress"] = "127.0.0.1", ["webDavUrl"] = "http://127.0.0.1:1/dav/", ["username"] = "existing-user" }.ToJsonString());
            var secrets = new InMemorySecretStore();
            var context = new ModuleContext("test", "1", "file-transfer", "file-transfer", root, root, root, "linux", ["secret.store"], new Dictionary<string, object> { ["secret.store"] = secrets });
            Assert.True((await module.InitializeAsync(context, default)).Ok);
            async Task<JsonObject> Call(string suffix, JsonObject? args = null)
            {
                var result = await module.ExecuteCommandAsync(new(Guid.NewGuid().ToString("N"), "file-transfer." + suffix, args ?? new()), default);
                Assert.True(result.Success, result.Output); return JsonNode.Parse(result.Output)!.AsObject();
            }
            var blocked = await Call("cloud.accounts.authorize.begin", new() { ["providerId"] = "quark" });
            Assert.Equal("blocked", blocked["state"]!.GetValue<string>()); Assert.Null(blocked["authorizationUrl"]);
            var operation = await Call("cloud.accounts.authorize.begin", new() { ["providerId"] = "baidu", ["nativeAuthorizationAvailable"] = true });
            Assert.Equal("waiting", operation["state"]!.GetValue<string>());
            var id = operation["operationId"]!.GetValue<string>();
            await Call("cloud.accounts.authorize.cancel", new() { ["operationId"] = id });
            var completion = await module.ExecuteCommandAsync(new("test", "file-transfer.cloud.accounts.authorize.complete", new() { ["operationId"] = id, ["credential"] = "never-log-this", ["credentialKind"] = "refreshToken" }), default);
            Assert.False(completion.Success); Assert.DoesNotContain("never-log-this", completion.Output);
            await Call("cloud.accounts.preferences", new() { ["mode"] = "cloudOnly" });
            var pendingPath = Path.Combine(root, "pending.txt");
            await File.WriteAllTextAsync(pendingPath, "pending cloud attachment");
            var sent = await Call("assistant.send", new() { ["paths"] = new JsonArray(pendingPath) });
            Assert.True(sent["accepted"]!.GetValue<bool>());
            var acceptedId = Assert.Single(sent["itemIds"]!.AsArray())!.GetValue<string>();
            var inspect = await Call("assistant.inspect");
            var pending = Assert.Single(inspect["items"]!.AsArray());
            Assert.Equal(acceptedId, pending!["id"]!.GetValue<string>());
            Assert.Equal("pending.txt", pending["name"]!.GetValue<string>());
            Assert.Equal("queued", pending["state"]!.GetValue<string>());
            var settings = (await Call("inspect"))["settings"]!;
            Assert.Equal("http://127.0.0.1:1/dav/", settings["webDavUrl"]!.GetValue<string>());
            Assert.Equal("existing-user", settings["username"]!.GetValue<string>());
            Assert.Empty((await Call("cloud.accounts.inspect"))["accounts"]!.AsArray());
        }
        finally { await module.DisposeAsync(default); PublicRelayClient.BaseAddressOverride = null; Directory.Delete(root, true); }
    }
}
