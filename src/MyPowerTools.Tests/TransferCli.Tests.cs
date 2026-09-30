using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MyPowerTools.Cli;

namespace MyPowerTools.Tests;

public class TransferCliTests
{
    private static JsonNode Snapshot(string state, string receiptDevice = "") => JsonNode.Parse("""
        {"items":[{"id":"item-1","state":"STATE","targetDeviceId":"pixel","receipts":RECEIPTS}]}
        """.Replace("STATE", state).Replace("RECEIPTS", receiptDevice.Length == 0 ? "[]" : $$"""[{"itemId":"item-1","deviceId":"{{receiptDevice}}","savedAt":"2026-09-30T00:00:00Z"}]"""))!;

    [Fact]
    public void QueueAndOtherMemberDoNotConfirmRequestedReceiver()
    {
        var result = TransferCli.Evaluate(Snapshot("stored", "laptop"), ["item-1"], "pixel", true);
        Assert.Equal(3, result.Item2);
        Assert.False(result.Item1["confirmed"]!.GetValue<bool>());
        Assert.True(result.Item1["timedOut"]!.GetValue<bool>());
    }

    [Fact]
    public void ReceiverReceiptConfirmsEvenIfStateStillStored()
    {
        var result = TransferCli.Evaluate(Snapshot("stored", "pixel"), ["item-1"], "pixel", true);
        Assert.Equal(0, result.Item2);
        Assert.True(result.Item1["confirmed"]!.GetValue<bool>());
        Assert.False(result.Item1["timedOut"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void TerminalFailureDoesNotBecomeTimeout(string state)
    {
        var result = TransferCli.Evaluate(Snapshot(state), ["item-1"], "pixel", true);
        Assert.Equal(1, result.Item2);
        Assert.False(result.Item1["timedOut"]!.GetValue<bool>());
    }

    [Fact]
    public async Task PrivateQuarkFailsBeforeEnqueueAndWritesOneJsonObject()
    {
        var invoker = new Fake(Snapshot("queued"));
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await TransferCli.RunAsync(["send", "--to", "pixel", "--text", "hello", "--via", "quark", "--json"], output, error, invoker);
        Assert.Equal(2, code);
        Assert.Empty(invoker.Commands);
        Assert.False(JsonNode.Parse(output.ToString())!["ok"]!.GetValue<bool>());
        Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task WaitTimeoutDoesNotCancelDurableItem()
    {
        var invoker = new Fake(Snapshot("queued"));
        var output = new StringWriter();
        var code = await TransferCli.RunAsync(["wait", "--item", "item-1", "--from", "pixel", "--timeout", "0.03", "--json"], output, new StringWriter(), invoker);
        Assert.Equal(3, code);
        Assert.DoesNotContain("file-transfer.assistant.cancel", invoker.Commands);
        Assert.True(JsonNode.Parse(output.ToString())!["data"]!["timedOut"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CancelUsesExistingModuleCommand()
    {
        var invoker = new Fake(Snapshot("queued"));
        var code = await TransferCli.RunAsync(["cancel", "--item", "item-1", "--json"], new StringWriter(), new StringWriter(), invoker);
        Assert.Equal(0, code);
        Assert.Equal("file-transfer.assistant.cancel", Assert.Single(invoker.Commands));
        Assert.Equal("item-1", invoker.LastArgs!["itemId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1e20")]
    [InlineData("4294968")]
    public async Task InvalidWaitTimeoutDoesNotEnqueue(string timeout)
    {
        var invoker = new Fake(Snapshot("queued"));
        var code = await TransferCli.RunAsync(["send", "--to", "pixel", "--text", "hello", "--wait", "--timeout", timeout, "--json"], new StringWriter(), new StringWriter(), invoker);
        Assert.Equal(2, code);
        Assert.Empty(invoker.Commands);
    }

    [Fact]
    public async Task WaitAcceptsOnlyRequestedReceiverAndStopsItsSubscription()
    {
        var invoker = new Fake(Snapshot("stored", "pixel"));
        var output = new StringWriter();
        var code = await TransferCli.RunAsync(["wait", "--item", "item-1", "--from", "pixel", "--json"], output, new StringWriter(), invoker);
        Assert.Equal(0, code);
        Assert.True(JsonNode.Parse(output.ToString())!["data"]!["confirmed"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task HelpNeedsNoRunner(string command)
    {
        var output = new StringWriter();
        var code = await TransferCli.RunAsync([command], output, new StringWriter());
        Assert.Equal(0, code);
        Assert.Equal("help", JsonNode.Parse(output.ToString())!["command"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("pair", "file-transfer.pair.import")]
    [InlineData("join", "file-transfer.assistant.link.import")]
    public async Task SecretInvitationRoutesWithoutPrintingCode(string operation, string expectedCommand)
    {
        var path = SecretFilePath("transfer-cli-invitation-");
        const string secret = "private-invitation-secret";
        try
        {
            await File.WriteAllTextAsync(path, secret);
            var invoker = new Fake(Snapshot("queued"));
            var output = new StringWriter();
            var diagnostics = new StringWriter();
            var code = await TransferCli.RunAsync([operation, "--code-file", path, "--json"], output, diagnostics, invoker);
            Assert.Equal(0, code);
            Assert.Equal(expectedCommand, Assert.Single(invoker.Commands));
            Assert.Equal(secret, invoker.LastArgs!["code"]!.GetValue<string>());
            Assert.DoesNotContain(secret, output.ToString());
            Assert.DoesNotContain(secret, diagnostics.ToString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("pair")]
    [InlineData("join")]
    public async Task SecretInvitationErrorSuppressesBackendDetails(string operation)
    {
        var path = SecretFilePath("transfer-cli-error-");
        try
        {
            await File.WriteAllTextAsync(path, "private-invitation-secret");
            var output = new StringWriter();
            var diagnostics = new StringWriter();
            var code = await TransferCli.RunAsync([operation, "--code-file", path, "--json"], output, diagnostics, new SecretFailure());
            Assert.Equal(1, code);
            Assert.DoesNotContain("private-invitation-secret", output.ToString());
            Assert.DoesNotContain("private-invitation-secret", diagnostics.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task InviteWritesPrivateFileAndOmitsCodeFromJson()
    {
        var path = SecretFilePath("transfer-cli-export-");
        try
        {
            var output = new StringWriter();
            var diagnostics = new StringWriter();
            var invoker = new ExportFake(true);
            var code = await TransferCli.RunAsync(["invite", "--output", path, "--json"], output, diagnostics, invoker);
            Assert.Equal(0, code);
            Assert.Equal("file-transfer.assistant.link.export", invoker.Command);
            Assert.Equal("private-invitation-secret", await File.ReadAllTextAsync(path));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(path, JsonNode.Parse(output.ToString())!["data"]!["filename"]!.GetValue<string>());
            Assert.Null(JsonNode.Parse(output.ToString())!["data"]!["code"]);
            Assert.DoesNotContain("private-invitation-secret", output.ToString());
            Assert.DoesNotContain("private-invitation-secret", diagnostics.ToString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InviteMissingCodeOrBackendFailureDoesNotLeakOrCreateFile(bool backendFailure)
    {
        var path = SecretFilePath("transfer-cli-export-error-");
        try
        {
            var output = new StringWriter();
            var diagnostics = new StringWriter();
            var code = await TransferCli.RunAsync(["invite", "--output", path, "--json"], output, diagnostics, new ExportFake(false, backendFailure));
            Assert.Equal(1, code);
            Assert.False(File.Exists(path));
            Assert.DoesNotContain("private-invitation-secret", output.ToString());
            Assert.DoesNotContain("private-invitation-secret", diagnostics.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task QuarkFirstPublishCanDiscoverCapabilityAfterEnqueue()
    {
        var path = SecretFilePath("transfer-cli-quark-first-");
        try
        {
            await File.WriteAllTextAsync(path, "first attachment");
            var invoker = new QuarkFake();
            var output = new StringWriter();
            var code = await TransferCli.RunAsync(["send", "--conversation", "shared", "--file", path, "--via", "quark", "--json"], output, new StringWriter(), invoker);
            Assert.Equal(0, code);
            Assert.True(invoker.Enqueued);
            Assert.True(JsonNode.Parse(output.ToString())!["data"]!["accepted"]!.GetValue<bool>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RefusedCancellationDoesNotReportSuccess()
    {
        var output = new StringWriter();
        var code = await TransferCli.RunAsync(["cancel", "--item", "already-saved", "--json"], output, new StringWriter(), new RejectedCancel());
        Assert.Equal(1, code);
        var result = JsonNode.Parse(output.ToString())!;
        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("cancellation_refused", result["error"]!["code"]!.GetValue<string>());
        Assert.False(result["data"]!["cancelled"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedSendPreservesIdsWhenReceiptWaitFails(bool initialInspectionTimesOut)
    {
        var invoker = new SendWaitFailure(initialInspectionTimesOut);
        var output = new StringWriter();
        var code = await TransferCli.RunAsync(["send", "--to", "pixel", "--text", "queued once", "--wait", "--timeout", "0.03", "--json"], output, new StringWriter(), invoker);
        Assert.NotEqual(0, code);
        var result = JsonNode.Parse(output.ToString())!;
        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("accepted-item", result["data"]!["itemIds"]![0]!.GetValue<string>());
        Assert.True(result["data"]!["accepted"]!.GetValue<bool>());
        Assert.Equal("receipt_wait_failed", result["error"]!["code"]!.GetValue<string>());
        Assert.Equal(1, invoker.SendCount);
        Assert.False(invoker.Cancelled);
    }

    private sealed class RejectedCancel : ITransferCommandInvoker
    {
        public Task<JsonNode> InvokeAsync(string command, JsonObject args, CancellationToken token) =>
            Task.FromResult(JsonNode.Parse("""{"itemId":"already-saved","cancelled":false}""")!);
        public async IAsyncEnumerable<bool> EventsAsync([EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class SendWaitFailure(bool slowInspect) : ITransferCommandInvoker
    {
        public int SendCount { get; private set; }
        public bool Cancelled { get; private set; }
        public async Task<JsonNode> InvokeAsync(string command, JsonObject args, CancellationToken token)
        {
            if (command.EndsWith("devices")) return JsonNode.Parse("""{"devices":[{"deviceId":"pixel","name":"Pixel"}]}""")!;
            if (command.EndsWith("assistant.send"))
            {
                SendCount++;
                return JsonNode.Parse("""{"accepted":true,"itemIds":["accepted-item"]}""")!;
            }
            if (command.EndsWith("assistant.cancel")) Cancelled = true;
            if (slowInspect) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new IOException("Receipt transport disconnected.");
        }
        public async IAsyncEnumerable<bool> EventsAsync([EnumeratorCancellation] CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            yield return true;
        }
    }

    private sealed class QuarkFake : ITransferCommandInvoker
    {
        public bool Enqueued { get; private set; }
        public Task<JsonNode> InvokeAsync(string command, JsonObject args, CancellationToken token)
        {
            if (command.EndsWith("cloud.accounts.inspect")) return Task.FromResult(JsonNode.Parse("""{"preferences":{"mode":"cloudOnly","defaultAccountId":"quark-account","transferAvailable":false},"accounts":[{"id":"quark-account","providerId":"quark","status":"ready"}]}""")!);
            if (command.EndsWith("assistant.inspect")) return Task.FromResult(JsonNode.Parse("""{"identity":{"conversationKey":"shared:self-test"}}""")!);
            Assert.Equal("file-transfer.assistant.send", command);
            Enqueued = true;
            return Task.FromResult(JsonNode.Parse("""{"accepted":true,"itemIds":["first-quark-item"],"targetDeviceId":null}""")!);
        }
        public async IAsyncEnumerable<bool> EventsAsync([EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ExportFake(bool includeCode, bool fail = false) : ITransferCommandInvoker
    {
        public string? Command { get; private set; }
        public Task<JsonNode> InvokeAsync(string command, JsonObject args, CancellationToken token)
        {
            Command = command;
            if (fail) throw new InvalidOperationException("private-invitation-secret");
            return Task.FromResult<JsonNode>(new JsonObject { ["code"] = includeCode ? "private-invitation-secret" : null, ["name"] = "Desktop", ["linkState"] = "waiting" });
        }
        public async IAsyncEnumerable<bool> EventsAsync([EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static string SecretFilePath(string prefix)
    {
        var directory = OperatingSystem.IsLinux() ? "/mnt/cache/data-cache" : Path.GetTempPath();
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, prefix + Guid.NewGuid().ToString("N"));
    }

    private sealed class SecretFailure : ITransferCommandInvoker
    {
        public Task<JsonNode> InvokeAsync(string command, JsonObject args, CancellationToken token) => throw new InvalidOperationException(args["code"]!.GetValue<string>());
        public async IAsyncEnumerable<bool> EventsAsync([EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class Fake(JsonNode snapshot) : ITransferCommandInvoker
    {
        public List<string> Commands { get; } = [];
        public JsonObject? LastArgs { get; private set; }
        public Task<JsonNode> InvokeAsync(string command, JsonObject args, CancellationToken token)
        {
            Commands.Add(command); LastArgs = args;
            if (command.EndsWith("assistant.cancel")) return Task.FromResult(JsonNode.Parse("""{"itemId":"item-1","cancelled":true}""")!);
            return Task.FromResult(command.EndsWith("devices") ? JsonNode.Parse("""{"devices":[{"deviceId":"pixel","name":"Pixel"}]}""")! : snapshot.DeepClone());
        }
        public async IAsyncEnumerable<bool> EventsAsync([EnumeratorCancellation] CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            yield return true;
        }
    }
}
