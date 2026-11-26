using System.Globalization;
using System.Runtime.CompilerServices;
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
    private readonly CancellationToken cancellationToken;

    public TransferCli(ITransferCommandInvoker invoker, CancellationToken cancellationToken = default)
    {
        this.invoker = invoker;
        this.cancellationToken = cancellationToken;
    }

    private Task<JsonNode> Call(string name, JsonObject? args = null, CancellationToken token = default)
    {
        var effectiveToken = token == default ? cancellationToken : token;
        return invoker.InvokeAsync("file-transfer." + name, args ?? new JsonObject(), effectiveToken);
    }

    public static int Run(string[] args)
    {
        return RunAsync(args, Console.Out, Console.Error).GetAwaiter().GetResult();
    }

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter diagnostics,
        ITransferCommandInvoker? injected = null,
        CancellationToken cancellationToken = default)
    {
        var command = args.FirstOrDefault() ?? "help";
        if (command is "-h" or "--help")
        {
            command = "help";
        }

        HostControlClient? client = null;
        try
        {
            var options = Options.Parse(args.Skip(1).ToArray());

            if (command == "help")
            {
                await WriteHelpAsync(output);
                return 0;
            }

            if (injected is null)
            {
                var address = options.One("--endpoint-address")
                    ?? Environment.GetEnvironmentVariable("MPT_ENDPOINT_ADDRESS");
                var endpoint = !string.IsNullOrEmpty(address)
                    ? new IpcEndpoint(
                        OperatingSystem.IsWindows() ? IpcTransport.NamedPipe : IpcTransport.UnixDomainSocket,
                        address)
                    : IpcEndpoint.RunnerDefault(PlatformId.Current());

                var dataRoot = options.One("--data-root")
                    ?? Environment.GetEnvironmentVariable("MPT_DATA_ROOT");
                var auth = HostControlAuthTokenStore.TryReadToken(dataRoot);
                if (string.IsNullOrWhiteSpace(auth))
                {
                    throw new InvalidOperationException(
                        "Runner authentication token is missing. Start the existing Runner and use its --data-root.");
                }

                client = HostControlClient.ForEndpoint(endpoint, auth);
                injected = new HostInvoker(client);
            }

            if (command is not ("cloud" or "public" or "drop") && options.Positional.Count != 0)
            {
                throw new ArgumentException("Unexpected positional argument: " + options.Positional[0]);
            }

            var cli = new TransferCli(injected, cancellationToken);
            var (data, code) = await cli.Execute(command, options);

            var envelope = new JsonObject
            {
                ["ok"] = code == 0,
                ["command"] = command,
                ["data"] = data,
                ["error"] = code == 0 ? null : BuildFailureError(command, code)
            };

            await output.WriteLineAsync(envelope.ToJsonString());
            return code;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Credential-bearing operations deliberately suppress backend exception text.
            var isSecretOperation = command is "pair" or "join" or "invite"
                || (command == "cloud" && args.Contains("complete"));
            var message = isSecretOperation
                ? "The secret-file operation failed. Check the file and Runner diagnostics."
                : ex.Message;

            await diagnostics.WriteLineAsync(message);

            var errorCode = ex switch
            {
                AcceptedSendWaitException => "receipt_wait_failed",
                ArgumentException => "invalid_arguments",
                _ => "command_failed"
            };

            var envelope = new JsonObject
            {
                ["ok"] = false,
                ["command"] = command,
                ["data"] = ex is AcceptedSendWaitException accepted ? accepted.SendResult : null,
                ["error"] = new JsonObject
                {
                    ["code"] = errorCode,
                    ["message"] = message
                }
            };

            await output.WriteLineAsync(envelope.ToJsonString());
            return ex is ArgumentException ? 2 : 1;
        }
        finally
        {
            client?.Dispose();
        }
    }

    private static Task WriteHelpAsync(TextWriter output)
    {
        var help = new JsonObject
        {
            ["ok"] = true,
            ["command"] = "help",
            ["data"] = new JsonObject
            {
                ["usage"] = "mpt transfer COMMAND [OPTIONS]",
                ["commands"] = new JsonArray(
                    "status",
                    "devices",
                    "conversations",
                    "send",
                    "receipts",
                    "wait",
                    "cancel",
                    "pair",
                    "join",
                    "invite",
                    "public",
                    "cloud",
                    "drop"),
                ["send"] = "send (--to DEVICE_ID | --conversation shared) [--text TEXT] [--file PATH ...] [--via auto|quark] [--wait --receipt-from DEVICE_ID --timeout SECONDS]",
                ["receipts"] = "receipts|wait --item ITEM_ID ... [--from DEVICE_ID] [--timeout SECONDS]",
                ["pair"] = "pair --code-file PATH (device pairing invitation)",
                ["join"] = "join --code-file PATH (private shared conversation invitation)",
                ["public"] = "public status|join|leave|private (server-authorized public room; no invitation needed)",
                ["invite"] = "invite --output PATH (writes a private shared invitation file; PATH must not exist)",
                ["cloud"] = "cloud accounts | authorize --provider quark|baidu | complete --operation ID --credential-file PATH --kind cookie|refreshToken | default --account ID | preferences --mode auto|cloud-only|public-relay",
                ["drop"] = "drop create --file PATH [--ttl SECONDS] [--max-downloads N] | list | revoke --id DROP_ID",
                ["common"] = "--json --endpoint-address ADDRESS --data-root ROOT; environment defaults: MPT_ENDPOINT_ADDRESS, MPT_DATA_ROOT"
            }
        };

        return output.WriteLineAsync(help.ToJsonString());
    }

    private static JsonObject BuildFailureError(string command, int code)
    {
        if (code == 3)
        {
            return new JsonObject
            {
                ["code"] = "receipt_timeout",
                ["message"] = "Receiver receipts are still pending; queued items remain durable."
            };
        }

        if (command == "cancel")
        {
            return new JsonObject
            {
                ["code"] = "cancellation_refused",
                ["message"] = "The item could not be cancelled. It may already have been saved by a receiver, or may not exist."
            };
        }

        if (command == "drop")
        {
            return new JsonObject
            {
                ["code"] = "drop_not_found",
                ["message"] = "The drop link could not be revoked. It may have already expired or does not exist."
            };
        }

        return new JsonObject
        {
            ["code"] = "delivery_failed",
            ["message"] = "An item failed, was cancelled, or could not be found."
        };
    }

    private async Task<(JsonNode, int)> Execute(string command, Options o)
    {
        switch (command)
        {
            case "status":
                return (await Call("assistant.inspect"), 0);

            case "devices":
                return (await Call("assistant.devices"), 0);

            case "conversations":
            {
                var inspect = await Call("assistant.inspect");
                var devices = await Call("assistant.devices");
                var result = new JsonObject
                {
                    ["shared"] = inspect["identity"]?.DeepClone(),
                    ["devices"] = devices["devices"]?.DeepClone()
                };
                return (result, 0);
            }

            case "send":
                return await SendAsync(o);

            case "receipts":
            case "wait":
            {
                var itemIds = o.Many("--item");
                if (itemIds.Length == 0)
                {
                    throw new ArgumentException("Provide at least one --item ITEM_ID.");
                }

                var receiptFrom = o.One("--from") is { } device
                    ? await ResolveDevice(device, receiptFilter: true)
                    : null;

                return command == "wait"
                    ? await Wait(itemIds, receiptFrom, o.Timeout())
                    : Evaluate(await Call("assistant.inspect"), itemIds, receiptFrom, false);
            }

            case "cancel":
            {
                var cancellation = await Call("assistant.cancel", new JsonObject
                {
                    ["itemId"] = o.Required("--item")
                });
                var cancelled = cancellation["cancelled"]?.GetValue<bool>() == true;
                return (cancellation, cancelled ? 0 : 1);
            }

            case "pair":
            {
                var codeText = (await File.ReadAllTextAsync(o.Required("--code-file"))).Trim();
                var pair = await Call("pair.import", new JsonObject
                {
                    ["code"] = codeText
                });
                return (pair, 0);
            }

            case "invite":
                return await InviteAsync(o);

            case "join":
            {
                var codeText = (await File.ReadAllTextAsync(o.Required("--code-file"))).Trim();
                var joined = await Call("assistant.link.import", new JsonObject
                {
                    ["code"] = codeText
                });
                return (joined, 0);
            }

            case "public":
            {
                var action = o.Positional.SingleOrDefault() ?? "status";
                if (action == "status")
                {
                    return (await Call("assistant.inspect"), 0);
                }

                if (action is not ("join" or "leave" or "private"))
                {
                    throw new ArgumentException("public status|join|leave|private");
                }

                var changed = await Call("assistant.public." + action);
                var identity = (await Call("assistant.inspect"))["identity"]?.DeepClone();
                return (new JsonObject
                {
                    ["result"] = changed,
                    ["identity"] = identity
                }, 0);
            }

            case "cloud":
                return await Cloud(o);

            case "drop":
                return await Drop(o);

            default:
                throw new ArgumentException(
                    "Commands: status, devices, conversations, send, receipts, wait, cancel, pair, join, invite, "
                    + "public status|join|leave|private, cloud accounts|authorize|complete|default|preferences, "
                    + "drop create|list|revoke. All output is JSON; use --json for explicit machine mode.");
        }
    }

    private async Task<(JsonNode, int)> SendAsync(Options o)
    {
        var to = o.One("--to");
        var conversation = o.One("--conversation");
        if ((to is null) == (conversation is null) || (conversation is not null && conversation != "shared"))
        {
            throw new ArgumentException("Choose exactly one of --to DEVICE_ID or --conversation shared.");
        }

        var via = o.One("--via") ?? "auto";
        if (via is not ("auto" or "quark"))
        {
            throw new ArgumentException("--via must be auto or quark.");
        }

        if (to is not null && via == "quark")
        {
            throw new ArgumentException("Quark attachments support shared conversations only; private sends cannot use Quark.");
        }

        if (to is not null && o.One("--receipt-from") is not null)
        {
            throw new ArgumentException("Private sends use the target device's receipts; --receipt-from is for shared sends.");
        }

        if (o.Flag("--wait") && to is null && o.One("--receipt-from") is null)
        {
            throw new ArgumentException("Shared --wait requires --receipt-from DEVICE_ID.");
        }

        var waitTimeout = o.Flag("--wait") ? o.Timeout() : 120;
        var text = o.One("--text");
        var paths = o.Many("--file").Select(Path.GetFullPath).ToArray();
        if (string.IsNullOrEmpty(text) && paths.Length == 0)
        {
            throw new ArgumentException("Provide --text or at least one --file.");
        }

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                throw new ArgumentException("File does not exist: " + path);
            }
        }

        var target = to is null ? null : await ResolveDevice(to);
        var receiver = target ?? (o.One("--receipt-from") is { } from
            ? await ResolveDevice(from, receiptFilter: true)
            : null);

        if (via == "quark")
        {
            var cloud = await Call("cloud.accounts.inspect");
            var selected = cloud["preferences"]?["defaultAccountId"]?.GetValue<string>();
            var mode = cloud["preferences"]?["mode"]?.GetValue<string>();
            var hasReadyQuark = cloud["accounts"]?.AsArray().Any(account =>
                account?["id"]?.GetValue<string>() == selected
                && account?["providerId"]?.GetValue<string>() == "quark"
                && account?["status"]?.GetValue<string>() == "ready") ?? false;

            if (mode != "cloudOnly" || !hasReadyQuark)
            {
                throw new ArgumentException(
                    "Quark requires a ready Quark default account and cloud-only mode. "
                    + "Use transfer cloud default --account ID and transfer cloud preferences --mode cloud-only explicitly.");
            }
            // transferAvailable is learned by the first backend publish; it cannot gate enqueue.
        }

        var pathArray = new JsonArray(paths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray());
        var sendArgs = new JsonObject
        {
            ["text"] = text,
            ["paths"] = pathArray
        };

        if (target is not null)
        {
            sendArgs["targetDeviceId"] = target;
        }
        else
        {
            var inspect = await Call("assistant.inspect");
            sendArgs["conversationKey"] = inspect["identity"]?["conversationKey"]?.DeepClone();
        }

        var sent = (await Call("assistant.send", sendArgs)).AsObject();
        if (!o.Flag("--wait"))
        {
            return (sent, 0);
        }

        var ids = sent["itemIds"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
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
    }

    private async Task<(JsonNode, int)> InviteAsync(Options o)
    {
        var invitationPath = Path.GetFullPath(o.Required("--output"));
        var invitation = await Call("assistant.link.export");
        var invitationCode = invitation["code"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(invitationCode))
        {
            throw new InvalidOperationException("Runner returned no shared invitation.");
        }

        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };

        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using (var file = new FileStream(invitationPath, fileOptions))
        await using (var writer = new StreamWriter(file))
        {
            await writer.WriteAsync(invitationCode);
        }

        var invitationResult = new JsonObject
        {
            ["filename"] = invitationPath
        };

        foreach (var field in new[] { "name", "address", "relayIncluded", "linked", "linkState" })
        {
            invitationResult[field] = invitation[field]?.DeepClone();
        }

        return (invitationResult, 0);
    }

    private async Task<(JsonNode, int)> Cloud(Options o)
    {
        var action = o.Positional.SingleOrDefault();
        switch (action)
        {
            case "accounts":
                return (await Call("cloud.accounts.inspect"), 0);

            case "default":
                return (await Call("cloud.accounts.default", new JsonObject
                {
                    ["accountId"] = o.Required("--account")
                }), 0);

            case "preferences":
            {
                var mode = o.Required("--mode");
                if (mode is not ("auto" or "cloud-only" or "public-relay"))
                {
                    throw new ArgumentException("--mode must be auto, cloud-only or public-relay.");
                }

                var wireMode = mode switch
                {
                    "cloud-only" => "cloudOnly",
                    "public-relay" => "publicRelay",
                    _ => "auto"
                };

                return (await Call("cloud.accounts.preferences", new JsonObject
                {
                    ["mode"] = wireMode
                }), 0);
            }

            case "authorize":
            {
                var provider = o.Required("--provider");
                if (provider is not ("quark" or "baidu"))
                {
                    throw new ArgumentException("--provider must be quark or baidu.");
                }

                return (await Call("cloud.accounts.authorize.begin", new JsonObject
                {
                    ["providerId"] = provider,
                    ["nativeAuthorizationAvailable"] = true
                }), 0);
            }

            case "complete":
            {
                var kind = o.Required("--kind");
                if (kind is not ("cookie" or "refreshToken"))
                {
                    throw new ArgumentException("--kind must be cookie or refreshToken.");
                }

                var credential = (await File.ReadAllTextAsync(o.Required("--credential-file"))).Trim();
                return (await Call("cloud.accounts.authorize.complete", new JsonObject
                {
                    ["operationId"] = o.Required("--operation"),
                    ["credentialKind"] = kind,
                    ["credential"] = credential
                }), 0);
            }

            default:
                throw new ArgumentException("Choose cloud accounts, authorize, complete, default, or preferences.");
        }
    }

    private async Task<(JsonNode, int)> Drop(Options o)
    {
        var action = o.Positional.SingleOrDefault();
        switch (action)
        {
            case "create":
            {
                var fullPath = Path.GetFullPath(o.Required("--file"));
                if (!File.Exists(fullPath))
                {
                    throw new ArgumentException("File does not exist: " + fullPath);
                }

                var args = new JsonObject
                {
                    ["path"] = fullPath
                };

                if (o.One("--ttl") is { } rawTtl)
                {
                    if (!int.TryParse(rawTtl, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ttlSeconds)
                        || ttlSeconds < 60
                        || ttlSeconds > 86400)
                    {
                        throw new ArgumentException("--ttl must be an integer between 60 and 86400 seconds.");
                    }

                    args["ttlSeconds"] = ttlSeconds;
                }

                if (o.One("--max-downloads") is { } rawMax)
                {
                    if (!int.TryParse(rawMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxDownloads)
                        || maxDownloads < 0)
                    {
                        throw new ArgumentException("--max-downloads must be 0 (unlimited) or a positive integer.");
                    }

                    args["maxDownloads"] = maxDownloads;
                }

                return (await Call("drop.create", args), 0);
            }

            case "list":
                return (await Call("drop.list"), 0);

            case "revoke":
            {
                var dropId = o.Required("--id");
                var response = await Call("drop.revoke", new JsonObject
                {
                    ["id"] = dropId
                });
                var revoked = response["revoked"]?.GetValue<bool>() == true;
                return (response, revoked ? 0 : 1);
            }

            default:
                throw new ArgumentException("Choose drop create, list, or revoke.");
        }
    }

    private async Task<string> ResolveDevice(string value, bool receiptFilter = false)
    {
        var devices = (await Call("assistant.devices"))["devices"]!.AsArray();
        if (devices.Any(d => d?["deviceId"]?.GetValue<string>() == value))
        {
            return value;
        }

        var matches = devices.Where(d => d?["name"]?.GetValue<string>() == value).ToArray();
        // Public-room receipts do not require private pairing or a contact-list entry.
        if (receiptFilter && matches.Length == 0)
        {
            return value;
        }

        if (matches.Length != 1)
        {
            var message = matches.Length == 0
                ? "Unknown device: " + value
                : "Ambiguous device name; use a device ID: " + value;
            throw new ArgumentException(message);
        }

        return matches[0]!["deviceId"]!.GetValue<string>();
    }

    public static (JsonNode, int) Evaluate(JsonNode snapshot, string[] ids, string? receiver, bool timedOut)
    {
        var items = new JsonArray();
        var allConfirmed = true;
        var anyFailed = false;

        foreach (var id in ids)
        {
            var item = snapshot["items"]?.AsArray()
                .FirstOrDefault(entry => entry?["id"]?.GetValue<string>() == id);
            var actualReceiver = receiver ?? item?["targetDeviceId"]?.GetValue<string>();
            if (actualReceiver is null)
            {
                throw new ArgumentException("Shared items require --from DEVICE_ID to identify the requested receiver.");
            }

            var state = item?["state"]?.GetValue<string>() ?? "missing";
            var receipts = item?["receipts"]?.AsArray() ?? new JsonArray();
            var confirmed = receipts.Any(receipt =>
                receipt?["deviceId"]?.GetValue<string>() == actualReceiver
                && receipt?["itemId"]?.GetValue<string>() == id
                && receipt?["savedAt"] is not null);

            allConfirmed &= confirmed;
            anyFailed |= !confirmed && state is "failed" or "cancelled" or "missing";

            items.Add(new JsonObject
            {
                ["itemId"] = id,
                ["state"] = state,
                ["receiverDeviceId"] = actualReceiver,
                ["confirmed"] = confirmed,
                ["receipts"] = receipts.DeepClone(),
                ["error"] = item?["error"]?.DeepClone()
            });
        }

        var exitCode = allConfirmed ? 0 : anyFailed ? 1 : timedOut ? 3 : 0;
        var summary = new JsonObject
        {
            ["items"] = items,
            ["confirmed"] = allConfirmed,
            ["timedOut"] = timedOut && !allConfirmed && !anyFailed
        };

        return (summary, exitCode);
    }

    private async Task<(JsonNode, int)> Wait(string[] ids, string? receiver, double timeout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));

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
                if (result.Item1["confirmed"]!.GetValue<bool>() || result.Item2 != 0)
                {
                    return result;
                }

                if (!await next)
                {
                    throw new IOException("Runner event stream closed while waiting for receiver receipts.");
                }

                next = events.MoveNextAsync().AsTask();
            }
        }
        catch (Exception ex) when (deadline.IsCancellationRequested && ex is OperationCanceledException or Grpc.Core.RpcException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (last is null)
            {
                throw new TimeoutException("Receipt inspection timed out; queued items remain durable.");
            }

            return Evaluate(last, ids, receiver, true);
        }
        finally
        {
            deadline.Cancel();
            try
            {
                await next;
            }
            catch (Exception)
            {
                // Suppress cleanup faults so they never mask the primary exception.
            }
        }
    }

    private sealed class AcceptedSendWaitException(JsonObject sendResult, Exception cause)
        : Exception(
            "The send was accepted, but receipt waiting failed. Query the returned item IDs instead of sending again. "
            + cause.Message,
            cause)
    {
        public JsonObject SendResult { get; } = sendResult;
    }

    public sealed class HostInvoker(HostControlClient client) : ITransferCommandInvoker
    {
        public async Task<JsonNode> InvokeAsync(string command, JsonObject args, CancellationToken token)
        {
            var response = await client.ExecuteCommandAsync(command, args, token);
            if (response.State != "succeeded")
            {
                var error = string.IsNullOrEmpty(response.ErrorMessage) ? response.Summary : response.ErrorMessage;
                throw new InvalidOperationException(error);
            }

            return JsonNode.Parse(response.Summary) ?? new JsonObject();
        }

        public async IAsyncEnumerable<bool> EventsAsync([EnumeratorCancellation] CancellationToken token)
        {
            await foreach (var evt in client.SubscribeHostEventsAsync(0, token))
            {
                // Command lifecycle events include our own inspect call; consuming them would
                // create an inspect/event feedback loop while the receiver is idle.
                if (evt.SourceId == "file-transfer" && evt.Type == "file-transfer.assistant.changed")
                {
                    yield return true;
                }
            }
        }
    }

    private sealed class Options
    {
        private static readonly HashSet<string> AllowedOptions =
        [
            "--json",
            "--wait",
            "--endpoint-address",
            "--data-root",
            "--to",
            "--conversation",
            "--text",
            "--file",
            "--receipt-from",
            "--via",
            "--timeout",
            "--item",
            "--from",
            "--code-file",
            "--provider",
            "--operation",
            "--credential-file",
            "--kind",
            "--account",
            "--mode",
            "--output",
            "--ttl",
            "--max-downloads",
            "--id"
        ];

        private readonly Dictionary<string, List<string>> values = new();

        public List<string> Positional { get; } = [];

        public string[] Many(string key)
        {
            return values.TryGetValue(key, out var list) ? list.ToArray() : [];
        }

        public string? One(string key)
        {
            return Many(key) switch
            {
                [] => null,
                [var single] => single,
                _ => throw new ArgumentException("Option must appear once: " + key)
            };
        }

        public string Required(string key)
        {
            return One(key) ?? throw new ArgumentException("Missing " + key);
        }

        public bool Flag(string key)
        {
            return values.ContainsKey(key);
        }

        public double Timeout()
        {
            var raw = One("--timeout") ?? "120";
            var maxSeconds = (uint.MaxValue - 1) / 1000d;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                || !double.IsFinite(number)
                || number <= 0
                || number > maxSeconds)
            {
                throw new ArgumentException("--timeout must be a positive number of seconds no greater than 4294967.294.");
            }

            return number;
        }

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var key = args[i];
                if (!key.StartsWith("--", StringComparison.Ordinal))
                {
                    options.Positional.Add(key);
                    continue;
                }

                if (!AllowedOptions.Contains(key))
                {
                    throw new ArgumentException("Unknown option: " + key);
                }

                string value;
                if (key is "--json" or "--wait")
                {
                    value = "true";
                }
                else
                {
                    if (++i >= args.Length)
                    {
                        throw new ArgumentException("Missing value for " + key);
                    }

                    value = args[i];
                }

                if (!options.values.TryGetValue(key, out var list))
                {
                    list = [];
                    options.values[key] = list;
                }

                list.Add(value);
            }

            return options;
        }
    }
}
