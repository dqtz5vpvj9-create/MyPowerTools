using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;
using FileTransfer.Core.Cloud;
using FileTransfer.MyPowerTools;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;
using Xunit.Abstractions;

namespace FileTransfer.Tests;

[CollectionDefinition("Cloud worker runtime", DisableParallelization = true)]
public sealed class CloudWorkerCollection;

public sealed class UnixCloudWorkerTheoryAttribute : TheoryAttribute
{
    public UnixCloudWorkerTheoryAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "The isolated runtime process fixture uses /bin/sh; no real OpenList account is used.";
    }
}

[Collection("Cloud worker runtime")]
public sealed class CloudWorkerTests(ITestOutputHelper output)
{
    [UnixCloudWorkerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_source_bounds_reads_and_polls_but_new_and_recovered_requests_finish(bool wrongLength)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-cloud-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var priorExecutable = Environment.GetEnvironmentVariable(OpenListRuntime.ExecutableVariable);
        var module = new FileTransferModule();
        var reads = new ConcurrentQueue<DateTimeOffset>();
        var polls = new ConcurrentQueue<DateTimeOffset>();
        var requests = new ConcurrentDictionary<string, CloudPayloadRequest>();
        var completed = new ConcurrentDictionary<string, byte[]>();
        var failedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reject = 1;
        await using var admin = new AssistantWebDavServer(Path.Combine(root, "admin"), OpenListRuntime.Port);
        await using var relay = new AssistantWebDavServer(Path.Combine(root, "relay"));
        static void Reply(System.Net.HttpListenerContext context, object body) => AssistantWebDavServer.Write(context, 200,
            JsonSerializer.SerializeToUtf8Bytes(body, AssistantJson.Options), "application/json");
        admin.Intercept = (context, request) =>
        {
            if (request.Path == "/ping") AssistantWebDavServer.Write(context, 200);
            else if (request.Path == "/api/auth/login") Reply(context, new { code = 200, data = new { token = "synthetic-admin" } });
            else if (request.Path == "/api/fs/get")
            {
                using var body = JsonDocument.Parse(context.Request.InputStream);
                var path = body.RootElement.GetProperty("path").GetString();
                if (path == "/mount/bad")
                {
                    reads.Enqueue(DateTimeOffset.UtcNow);
                    failedOnce.TrySetResult();
                    if (Volatile.Read(ref reject) == 1 && !wrongLength)
                    {
                        Reply(context, new { code = 403, message = "login expired" });
                        return true;
                    }
                }
                Reply(context, new { code = 200, data = new { raw_url = $"http://127.0.0.1:{admin.Port}/p/" + (path == "/mount/bad" ? "bad" : "good") } });
            }
            else if (request.Path.StartsWith("/p/", StringComparison.Ordinal))
                AssistantWebDavServer.Write(context, 200, Encoding.UTF8.GetBytes(request.Path == "/p/bad" && Volatile.Read(ref reject) == 1 ? "wrong length" : "body"));
            else AssistantWebDavServer.Write(context, 404);
            return true;
        };
        relay.Intercept = (context, request) =>
        {
            if (request.Path == "/mpt/relay/v1/cloud/requests")
            {
                polls.Enqueue(DateTimeOffset.UtcNow);
                Reply(context, new { requests = requests.Values.ToArray(), serverTime = DateTimeOffset.UtcNow });
            }
            else if (request.Method == "PUT" && request.Path.StartsWith("/mpt/relay/v1/cloud/requests/", StringComparison.Ordinal))
            {
                var id = request.Path.Split('/')[6];
                using var body = new MemoryStream(); context.Request.InputStream.CopyTo(body);
                completed[id] = body.ToArray();
                requests.TryRemove(id, out _);
                AssistantWebDavServer.Write(context, 200);
            }
            else if (request.Path == PublicRelayClient.ConversationsPath) Reply(context, new { });
            else if (request.Path == "/mpt/relay/health") Reply(context, new { capabilities = new { cloudPayloadStream = 1 } });
            else AssistantWebDavServer.Write(context, 404);
            return true;
        };
        try
        {
            // The runtime's child process is owned by this module. Its admin API is the real HTTP
            // listener above, so no provider login, existing process or user file is touched.
            var executable = Path.Combine(root, "runtime.sh");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nif [ \"$1\" = admin ]; then mkdir -p \"$4\"; touch \"$4/data.db\"; printf 'password: synthetic-admin\\n'; else exec sleep 3600; fi\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable(OpenListRuntime.ExecutableVariable, executable);
            PublicRelayClient.BaseAddressOverride = () => new Uri(relay.Url);
            InboxRelays.EndpointsOverride = () => [new(InboxRelays.Tail, new Uri("http://127.0.0.1:1/")), new(InboxRelays.Public, new Uri(relay.Url))];
            var data = Path.Combine(root, "device"); Directory.CreateDirectory(data);
            const string conversation = "cloud-worker-test";
            var key = new string('a', 64);
            var secrets = new InMemorySecretStore();
            await secrets.SaveAsync("file-transfer", "conversation-id", conversation, default);
            await secrets.SaveAsync("file-transfer", "conversation-key", key, default);
            await File.WriteAllTextAsync(Path.Combine(data, "preferences.json"), new JsonObject
            {
                ["deviceId"] = "cloud-worker", ["listenAddress"] = "127.0.0.199", ["receiveDirectory"] = Path.Combine(data, "received")
            }.ToJsonString());
            await new CloudAccountStore(Path.Combine(data, "cloud-accounts.json")).SaveAsync(new(
                [new CloudAccount("account", "quark", "QA", "ready", "/mount", "QA", MountPath: "/mount")],
                new CloudPreferences("account", "cloudOnly")), default);
            var store = new AssistantStore(Path.Combine(data, "assistant"));
            var source = Path.Combine(root, "source.txt"); await File.WriteAllTextAsync(source, "body");
            var queued = await store.EnqueueAsync(new("cloud-worker", "Cloud worker", conversation), AssistantDraft.ForPaths([source, source]), default);
            // A previously uploaded pair of files; this test exercises the actual module's source
            // serving worker, independently of its upload scheduler.
            await store.MutateAsync(state => { foreach (var item in state.Items) item.State = AssistantItemState.Stored; }, default);
            var mappings = new CloudPayloadStore(Path.Combine(data, "cloud-payloads.json"));
            var offers = queued.Select(item => CloudAttachmentOffer.Create(conversation, item.ToManifest())).ToArray();
            Assert.Equal(2, offers.Length);
            for (var i = 0; i < offers.Length; i++)
                await mappings.SaveAsync(new(offers[i], "account", "/mount", i == 0 ? "/mount/bad" : "/mount/good", true), default);
            requests["bad-request"] = new("bad-request", offers[0].Message.Id, offers[0].Capability, 4, DateTimeOffset.UtcNow.AddSeconds(30));
            var context = new ModuleContext("test", "1", "file-transfer", "file-transfer", data, data, data, "linux", ["secret.store"],
                new Dictionary<string, object> { ["secret.store"] = secrets });
            Assert.True((await module.InitializeAsync(context, default)).Ok);
            await failedOnce.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(3200);
            output.WriteLine($"wrongLength={wrongLength}; window=3.2s; sourceReads={reads.Count}; requestPolls={polls.Count}");
            Assert.InRange(reads.Count, 1, 3);
            Assert.InRange(polls.Count, 1, 6);
            var attempts = reads.ToArray();
            for (var i = 1; i < attempts.Length; i++) Assert.True(attempts[i] - attempts[i - 1] >= TimeSpan.FromMilliseconds(1900));

            var watch = Stopwatch.StartNew();
            requests["good-request"] = new("good-request", offers[1].Message.Id, offers[1].Capability, 4, DateTimeOffset.UtcNow.AddSeconds(30));
            await UntilAsync(() => completed.ContainsKey("good-request"), TimeSpan.FromSeconds(3));
            output.WriteLine($"newRequestMs={watch.Elapsed.TotalMilliseconds:F0}");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
            Assert.Equal("body", Encoding.UTF8.GetString(completed["good-request"]));
            Volatile.Write(ref reject, 0);
            await UntilAsync(() => completed.ContainsKey("bad-request"), TimeSpan.FromSeconds(3));
            output.WriteLine("originalRequestRecovered=true");
            Assert.Equal("body", Encoding.UTF8.GetString(completed["bad-request"]));
            var persisted = await new AssistantStore(Path.Combine(data, "assistant")).LoadAsync(default);
            Assert.Equal(queued.Select(item => item.Id).Order(), persisted.Items.Select(item => item.Id).Order());
        }
        finally
        {
            await module.DisposeAsync(default);
            PublicRelayClient.BaseAddressOverride = null;
            InboxRelays.EndpointsOverride = null;
            Environment.SetEnvironmentVariable(OpenListRuntime.ExecutableVariable, priorExecutable);
            await admin.DisposeAsync(); await relay.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < timeout) await Task.Delay(25);
        Assert.True(condition(), "The request did not complete within the bounded wait.");
    }
}
