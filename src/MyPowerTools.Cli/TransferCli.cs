using System.Globalization;
using System.Text.Json.Nodes;
using MyPowerTools.HostControl;
using MyPowerTools.Ipc;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Cli;

public interface ITransferCommandInvoker
{
    Task<JsonNode> InvokeAsync(string command, JsonObject args, CancellationToken token);
    IAsyncEnumerable<bool> EventsAsync(CancellationToken token);
}

public sealed class TransferCli
{
    private readonly ITransferCommandInvoker invoker;
    public TransferCli(ITransferCommandInvoker invoker) => this.invoker = invoker;
    private Task<JsonNode> Call(string name, JsonObject? args = null, CancellationToken token = default) =>
        invoker.InvokeAsync("file-transfer." + name, args ?? new(), token);

    public static int Run(string[] args)
    {
        return RunAsync(args, Console.Out, Console.Error).GetAwaiter().GetResult();
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter diagnostics, ITransferCommandInvoker? injected = null)
    {
        var command = args.FirstOrDefault() ?? "help";
        if (command is "-h" or "--help") command = "help";
        HostControlClient? client = null;
        try
        {
            var options = Options.Parse(args.Skip(1).ToArray());
            if (command == "help")
            {
                await output.WriteLineAsync(new JsonObject
                {
                    ["ok"] = true, ["command"] = "help", ["data"] = new JsonObject
                    {
                        ["usage"] = "mpt transfer COMMAND [OPTIONS]",
                        ["commands"] = new JsonArray("status", "devices", "conversations", "send", "receipts", "wait", "cancel", "pair", "join", "invite", "cloud"),
                        ["send"] = "send (--to DEVICE_ID | --conversation shared) [--text TEXT] [--file PATH ...] [--via auto|quark] [--wait --receipt-from DEVICE_ID --timeout SECONDS]",
                        ["receipts"] = "receipts|wait --item ITEM_ID ... [--from DEVICE_ID] [--timeout SECONDS]",
                        ["pair"] = "pair --code-file PATH (device pairing invitation)",
                        ["join"] = "join --code-file PATH (shared conversation invitation)",
                        ["invite"] = "invite --output PATH (writes a private shared invitation file; PATH must not exist)",
                        ["cloud"] = "cloud accounts | authorize --provider quark|baidu | complete --operation ID --credential-file PATH --kind cookie|refreshToken | default --account ID | preferences --mode auto|cloud-only",
                        ["common"] = "--json --endpoint-address ADDRESS --data-root ROOT; environment defaults: MPT_ENDPOINT_ADDRESS, MPT_DATA_ROOT"
                    }
                }.ToJsonString());
                return 0;
            }
            if (injected is null)
            {
                var endpoint = (options.One("--endpoint-address") ?? Environment.GetEnvironmentVariable("MPT_ENDPOINT_ADDRESS")) is { Length: > 0 } address
                    ? new IpcEndpoint(OperatingSystem.IsWindows() ? IpcTransport.NamedPipe : IpcTransport.UnixDomainSocket, address)
                    : IpcEndpoint.RunnerDefault(PlatformId.Current());
                var auth = HostControlAuthTokenStore.TryReadToken(options.One("--data-root") ?? Environment.GetEnvironmentVariable("MPT_DATA_ROOT"));
                if (string.IsNullOrWhiteSpace(auth)) throw new InvalidOperationException("Runner authentication token is missing. Start the existing Runner and use its --data-root.");
                client = HostControlClient.ForEndpoint(endpoint, auth);
                injected = new HostInvoker(client);
            }
            if (command != "cloud" && options.Positional.Count != 0) throw new ArgumentException("Unexpected positional argument: " + options.Positional[0]);
            var cli = new TransferCli(injected);
            var (data, code) = await cli.Execute(command, options);
            await output.WriteLineAsync(new JsonObject { ["ok"] = code == 0, ["command"] = command, ["data"] = data,
                ["error"] = code == 0 ? null : new JsonObject { ["code"] = code == 3 ? "receipt_timeout" : command == "cancel" ? "cancellation_refused" : "delivery_failed", ["message"] = code == 3 ? "Receiver receipts are still pending; queued items remain durable." : command == "cancel" ? "The item could not be cancelled. It may already have been saved by a receiver, or may not exist." : "An item failed, was cancelled, or could not be found." } }.ToJsonString());
            return code;
        }
        catch (Exception ex)
        {
            // Credential-bearing operations deliberately suppress backend exception text.
            var message = command is "pair" or "join" or "invite" || (command == "cloud" && args.Contains("complete"))
                ? "The secret-file operation failed. Check the file and Runner diagnostics." : ex.Message;
            await diagnostics.WriteLineAsync(message);
            await output.WriteLineAsync(new JsonObject { ["ok"] = false, ["command"] = command,
                ["data"] = ex is AcceptedSendWaitException accepted ? accepted.SendResult : null,
                ["error"] = new JsonObject { ["code"] = ex is AcceptedSendWaitException ? "receipt_wait_failed" : ex is ArgumentException ? "invalid_arguments" : "command_failed", ["message"] = message } }.ToJsonString());
            return ex is ArgumentException ? 2 : 1;
        }
        finally { client?.Dispose(); }
    }

    private async Task<(JsonNode, int)> Execute(string command, Options o)
    {
        switch (command)
        {
            case "status": return (await Call("assistant.inspect"), 0);
            case "devices": return (await Call("assistant.devices"), 0);
            case "conversations":
                var inspect = await Call("assistant.inspect");
                var devices = await Call("assistant.devices");
                return (new JsonObject { ["shared"] = inspect["identity"]?.DeepClone(), ["devices"] = devices["devices"]?.DeepClone() }, 0);
            case "send":
                var to = o.One("--to");
                var conversation = o.One("--conversation");
                if ((to is null) == (conversation is null) || (conversation is not null && conversation != "shared"))
                    throw new ArgumentException("Choose exactly one of --to DEVICE_ID or --conversation shared.");
                var via = o.One("--via") ?? "auto";
                if (via is not ("auto" or "quark")) throw new ArgumentException("--via must be auto or quark.");
                if (to is not null && via == "quark") throw new ArgumentException("Quark attachments support shared conversations only; private sends cannot use Quark.");
                if (to is not null && o.One("--receipt-from") is not null) throw new ArgumentException("Private sends use the target device's receipts; --receipt-from is for shared sends.");
                if (o.Flag("--wait") && to is null && o.One("--receipt-from") is null) throw new ArgumentException("Shared --wait requires --receipt-from DEVICE_ID.");
                var waitTimeout = o.Flag("--wait") ? o.Timeout() : 120;
                var text = o.One("--text");
                var paths = o.Many("--file").Select(Path.GetFullPath).ToArray();
                if (string.IsNullOrEmpty(text) && paths.Length == 0) throw new ArgumentException("Provide --text or at least one --file.");
                foreach (var path in paths) if (!File.Exists(path)) throw new ArgumentException("File does not exist: " + path);
                var target = to is null ? null : await ResolveDevice(to);
                var receiver = target ?? (o.One("--receipt-from") is { } from ? await ResolveDevice(from) : null);
                if (via == "quark")
                {
                    var cloud = await Call("cloud.accounts.inspect");
                    var selected = cloud["preferences"]?["defaultAccountId"]?.GetValue<string>();
                    if (cloud["preferences"]?["mode"]?.GetValue<string>() != "cloudOnly" ||
                        !(cloud["accounts"]?.AsArray().Any(a => a?["id"]?.GetValue<string>() == selected && a?["providerId"]?.GetValue<string>() == "quark" && a?["status"]?.GetValue<string>() == "ready") ?? false))
                        throw new ArgumentException("Quark requires a ready Quark default account and cloud-only mode. Use transfer cloud default --account ID and transfer cloud preferences --mode cloud-only explicitly.");
                    // transferAvailable is learned by the first backend publish; it cannot gate enqueue.
                }
                var sendArgs = new JsonObject { ["text"] = text, ["paths"] = new JsonArray(paths.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()) };
                if (target is not null) sendArgs["targetDeviceId"] = target;
                else sendArgs["conversationKey"] = (await Call("assistant.inspect"))["identity"]?["conversationKey"]?.DeepClone();
                var sent = (await Call("assistant.send", sendArgs)).AsObject();
                if (!o.Flag("--wait")) return (sent, 0);
                var ids = sent["itemIds"]!.AsArray().Select(i => i!.GetValue<string>()).ToArray();
                try
                {
                    var waited = await Wait(ids, receiver, waitTimeout);
                    sent["delivery"] = waited.Item1;
                    return (sent, waited.Item2);
                }
                catch (Exception ex)
                {
                    throw new AcceptedSendWaitException(sent, ex);
                }
            case "receipts":
            case "wait":
                var itemIds = o.Many("--item");
                if (itemIds.Length == 0) throw new ArgumentException("Provide at least one --item ITEM_ID.");
                var receiptFrom = o.One("--from") is { } device ? await ResolveDevice(device) : null;
                return command == "wait" ? await Wait(itemIds, receiptFrom, o.Timeout()) : Evaluate(await Call("assistant.inspect"), itemIds, receiptFrom, false);
            case "cancel":
                var cancellation = await Call("assistant.cancel", new() { ["itemId"] = o.Required("--item") });
                return (cancellation, cancellation["cancelled"]?.GetValue<bool>() == true ? 0 : 1);
            case "pair":
                var pair = await Call("pair.import", new() { ["code"] = (await File.ReadAllTextAsync(o.Required("--code-file"))).Trim() });
                return (pair, 0);
            case "invite":
                var invitationPath = Path.GetFullPath(o.Required("--output"));
                var invitation = await Call("assistant.link.export");
                var invitationCode = invitation["code"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(invitationCode)) throw new InvalidOperationException("Runner returned no shared invitation.");
                var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var file = new FileStream(invitationPath, fileOptions))
                await using (var writer = new StreamWriter(file))
                    await writer.WriteAsync(invitationCode);
                var invitationResult = new JsonObject { ["filename"] = invitationPath };
                foreach (var field in new[] { "name", "address", "relayIncluded", "linked", "linkState" })
                    invitationResult[field] = invitation[field]?.DeepClone();
                return (invitationResult, 0);
            case "join":
                return (await Call("assistant.link.import", new() { ["code"] = (await File.ReadAllTextAsync(o.Required("--code-file"))).Trim() }), 0);
            case "cloud": return await Cloud(o);
            default: throw new ArgumentException("Commands: status, devices, conversations, send, receipts, wait, cancel, pair, join, invite, cloud accounts|authorize|complete|default|preferences. All output is JSON; use --json for explicit machine mode.");
        }
    }

    private async Task<(JsonNode, int)> Cloud(Options o)
    {
        switch (o.Positional.SingleOrDefault())
        {
            case "accounts": return (await Call("cloud.accounts.inspect"), 0);
            case "default": return (await Call("cloud.accounts.default", new() { ["accountId"] = o.Required("--account") }), 0);
            case "preferences":
                var mode = o.Required("--mode");
                if (mode is not ("auto" or "cloud-only")) throw new ArgumentException("--mode must be auto or cloud-only.");
                return (await Call("cloud.accounts.preferences", new() { ["mode"] = mode == "cloud-only" ? "cloudOnly" : "auto" }), 0);
            case "authorize":
                var provider = o.Required("--provider");
                if (provider is not ("quark" or "baidu")) throw new ArgumentException("--provider must be quark or baidu.");
                return (await Call("cloud.accounts.authorize.begin", new() { ["providerId"] = provider, ["nativeAuthorizationAvailable"] = true }), 0);
            case "complete":
                var kind = o.Required("--kind");
                if (kind is not ("cookie" or "refreshToken")) throw new ArgumentException("--kind must be cookie or refreshToken.");
                return (await Call("cloud.accounts.authorize.complete", new() { ["operationId"] = o.Required("--operation"),
                    ["credentialKind"] = kind, ["credential"] = (await File.ReadAllTextAsync(o.Required("--credential-file"))).Trim() }), 0);
            default: throw new ArgumentException("Choose cloud accounts, authorize, complete, default, or preferences.");
        }
    }

    private async Task<string> ResolveDevice(string value)
    {
        var devices = (await Call("assistant.devices"))["devices"]!.AsArray();
        if (devices.Any(d => d?["deviceId"]?.GetValue<string>() == value)) return value;
        var matches = devices.Where(d => d?["name"]?.GetValue<string>() == value).ToArray();
        if (matches.Length != 1) throw new ArgumentException(matches.Length == 0 ? "Unknown device: " + value : "Ambiguous device name; use a device ID: " + value);
        return matches[0]!["deviceId"]!.GetValue<string>();
    }

    public static (JsonNode, int) Evaluate(JsonNode snapshot, string[] ids, string? receiver, bool timedOut)
    {
        var items = new JsonArray();
        bool all = true, failed = false;
        foreach (var id in ids)
        {
            var item = snapshot["items"]?.AsArray().FirstOrDefault(i => i?["id"]?.GetValue<string>() == id);
            var actualReceiver = receiver ?? item?["targetDeviceId"]?.GetValue<string>();
            if (actualReceiver is null) throw new ArgumentException("Shared items require --from DEVICE_ID to identify the requested receiver.");
            var state = item?["state"]?.GetValue<string>() ?? "missing";
            var receipts = item?["receipts"]?.AsArray() ?? new JsonArray();
            bool confirmed = receipts.Any(r => r?["deviceId"]?.GetValue<string>() == actualReceiver && r?["itemId"]?.GetValue<string>() == id && r?["savedAt"] is not null);
            all &= confirmed;
            failed |= !confirmed && state is "failed" or "cancelled" or "missing";
            items.Add(new JsonObject { ["itemId"] = id, ["state"] = state, ["receiverDeviceId"] = actualReceiver,
                ["confirmed"] = confirmed, ["receipts"] = receipts.DeepClone(), ["error"] = item?["error"]?.DeepClone() });
        }
        return (new JsonObject { ["items"] = items, ["confirmed"] = all, ["timedOut"] = timedOut && !all && !failed }, all ? 0 : failed ? 1 : timedOut ? 3 : 0);
    }

    private async Task<(JsonNode, int)> Wait(string[] ids, string? receiver, double timeout)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        // Start subscription before inspecting; replay from sequence zero closes the subscribe/read race.
        await using var events = invoker.EventsAsync(deadline.Token).GetAsyncEnumerator(deadline.Token);
        var next = events.MoveNextAsync().AsTask();
        JsonNode? last = null;
        try
        {
            while (true)
            {
                last = await Call("assistant.inspect", token: deadline.Token);
                var result = Evaluate(last, ids, receiver, false);
                if (result.Item1["confirmed"]!.GetValue<bool>() || result.Item2 != 0) return result;
                if (!await next) throw new IOException("Runner event stream closed while waiting for receiver receipts.");
                next = events.MoveNextAsync().AsTask();
            }
        }
        catch (Exception ex) when (deadline.IsCancellationRequested && ex is OperationCanceledException or Grpc.Core.RpcException)
        {
            if (last is null) throw new TimeoutException("Receipt inspection timed out; queued items remain durable.");
            return Evaluate(last, ids, receiver, true);
        }
        finally
        {
            deadline.Cancel();
            try { await next; } catch (OperationCanceledException) { } catch (Grpc.Core.RpcException) { }
        }
    }

    private sealed class AcceptedSendWaitException(JsonObject sendResult, Exception cause)
        : Exception("The send was accepted, but receipt waiting failed. Query the returned item IDs instead of sending again. " + cause.Message, cause)
    {
        public JsonObject SendResult { get; } = sendResult;
    }

    private sealed class HostInvoker(HostControlClient client) : ITransferCommandInvoker
    {
        public async Task<JsonNode> InvokeAsync(string command, JsonObject args, CancellationToken token)
        {
            var response = await client.ExecuteCommandAsync(command, args, token);
            if (response.State != "succeeded") throw new InvalidOperationException(string.IsNullOrEmpty(response.ErrorMessage) ? response.Summary : response.ErrorMessage);
            return JsonNode.Parse(response.Summary) ?? new JsonObject();
        }
        public async IAsyncEnumerable<bool> EventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var evt in client.SubscribeHostEventsAsync(0, token))
                // Command lifecycle events include our own inspect call; consuming them would
                // create an inspect/event feedback loop while the receiver is idle.
                if (evt.SourceId == "file-transfer" && evt.Type == "file-transfer.assistant.changed") yield return true;
        }
    }

    private sealed class Options
    {
        private readonly Dictionary<string, List<string>> values = new();
        public List<string> Positional { get; } = [];
        public string[] Many(string key) => values.TryGetValue(key, out var list) ? list.ToArray() : [];
        public string? One(string key) => Many(key) switch { [] => null, [var single] => single, _ => throw new ArgumentException("Option must appear once: " + key) };
        public string Required(string key) => One(key) ?? throw new ArgumentException("Missing " + key);
        public bool Flag(string key) => values.ContainsKey(key);
        public double Timeout()
        {
            var raw = One("--timeout") ?? "120";
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number <= 0 || number > (uint.MaxValue - 1) / 1000d)
                throw new ArgumentException("--timeout must be a positive number of seconds no greater than 4294967.294.");
            return number;
        }
        public static Options Parse(string[] args)
        {
            var o = new Options();
            var allowed = new HashSet<string> { "--json", "--wait", "--endpoint-address", "--data-root", "--to", "--conversation", "--text", "--file", "--receipt-from", "--via", "--timeout", "--item", "--from", "--code-file", "--provider", "--operation", "--credential-file", "--kind", "--account", "--mode", "--output" };
            for (int i = 0; i < args.Length; i++)
            {
                var key = args[i];
                if (!key.StartsWith("--")) { o.Positional.Add(key); continue; }
                if (!allowed.Contains(key)) throw new ArgumentException("Unknown option: " + key);
                var value = key is "--json" or "--wait" ? "true" : ++i < args.Length && !args[i].StartsWith("--") ? args[i] : throw new ArgumentException("Missing value for " + key);
                if (!o.values.TryGetValue(key, out var list)) o.values[key] = list = [];
                list.Add(value);
            }
            return o;
        }
    }
}
