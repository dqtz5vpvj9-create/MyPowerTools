using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
namespace MyPowerTools.TransferMcp;
[McpServerToolType]
public sealed class TransferTools(TransferAccess access)
{
    private static string Wire<T>(T value) => JsonSerializer.Serialize(value).Trim('"');
    [McpServerTool(Name = "mpt_status", ReadOnly = true, Destructive = false)]
    [Description("查看本机 MPT 传输服务及文件助手状态。不会启动第二个传输实例。")]
    public async Task<CallToolResult> mpt_status(CancellationToken cancellationToken = default)
    {
        return await access.Call(["status"], cancellationToken);
    }
    [McpServerTool(Name = "mpt_devices", ReadOnly = true, Destructive = false)]
    [Description("列出设备及稳定 deviceId。发送前使用此工具选择真实接收设备。")]
    public async Task<CallToolResult> mpt_devices(CancellationToken cancellationToken = default)
    {
        return await access.Call(["devices"], cancellationToken);
    }
    [McpServerTool(Name = "mpt_conversations", ReadOnly = true, Destructive = false)]
    [Description("列出文件助手共享会话和设备私聊。共享会话对其全部成员可见。")]
    public async Task<CallToolResult> mpt_conversations(CancellationToken cancellationToken = default)
    {
        return await access.Call(["conversations"], cancellationToken);
    }
    [McpServerTool(Name = "mpt_send_files", ReadOnly = false, Destructive = false)]
    [Description("向一个已配对设备私聊发送本机文件/文字。成功等待要求该设备真实接收回执。\n\nfiles 是 MCP 所在主机的路径。此私聊通道尚不支持夸克中转；不要把私聊\n自动改成共享会话。超时仍保留待发项，可用 mpt_wait_receipts 查询。")]
    public async Task<CallToolResult> mpt_send_files(string device, string[] files, string text = "", bool wait_for_receipt = true, int timeout_seconds = 600, CancellationToken cancellationToken = default)
    {
        List<string> args = ["send", "--to", device];
        if (!string.IsNullOrEmpty(text)) args.AddRange(["--text", text]);
        foreach (var path in files) args.AddRange(["--file", path]);
        if (wait_for_receipt) args.AddRange(["--wait", "--timeout", timeout_seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        return await access.Call(args.ToArray(), cancellationToken);
    }
    [McpServerTool(Name = "mpt_send_shared", ReadOnly = false, Destructive = false)]
    [Description("向文件助手公屏发送内容，全部共享成员可见；不是指定设备的私聊。\n\n大文件可选 via=quark，要求本机已连接夸克、设为默认并开启 cloud-only。\nreceipt_from 指定要等待回执的设备，不改变公屏可见范围。超时不删除待发项。")]
    public async Task<CallToolResult> mpt_send_shared(string[] files, string text = "", string receipt_from = "", RelayRoute via = RelayRoute.Auto, bool wait_for_receipt = true, int timeout_seconds = 600, CancellationToken cancellationToken = default)
    {
        List<string> args = ["send", "--conversation", "shared", "--via", Wire(via)];
        if (!string.IsNullOrEmpty(text)) args.AddRange(["--text", text]);
        foreach (var path in files) args.AddRange(["--file", path]);
        if (wait_for_receipt) args.AddRange(["--wait", "--timeout", timeout_seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (!string.IsNullOrEmpty(receipt_from)) args.AddRange(["--receipt-from", receipt_from]);
        return await access.Call(args.ToArray(), cancellationToken);
    }
    [McpServerTool(Name = "mpt_receipts", ReadOnly = true, Destructive = false)]
    [Description("查询各条消息的真实接收回执。已排队或已上传不等于已送达。")]
    public async Task<CallToolResult> mpt_receipts(string[] item_ids, string receiver = "", CancellationToken cancellationToken = default)
    {
        List<string> args = ["receipts"];
        foreach (var item in item_ids) args.AddRange(["--item", item]);
        if (!string.IsNullOrEmpty(receiver)) args.AddRange(["--from", receiver]);
        return await access.Call(args.ToArray(), cancellationToken);
    }
    [McpServerTool(Name = "mpt_wait_receipts", ReadOnly = true, Destructive = false)]
    [Description("等待每条消息的接收回执；指定 receiver 时其他设备的回执不算成功。")]
    public async Task<CallToolResult> mpt_wait_receipts(string[] item_ids, string receiver = "", int timeout_seconds = 600, CancellationToken cancellationToken = default)
    {
        List<string> args = ["wait", "--timeout", timeout_seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        foreach (var item in item_ids) args.AddRange(["--item", item]);
        if (!string.IsNullOrEmpty(receiver)) args.AddRange(["--from", receiver]);
        return await access.Call(args.ToArray(), cancellationToken);
    }
    [McpServerTool(Name = "mpt_cancel", ReadOnly = false, Destructive = true)]
    [Description("取消指定待发消息。不会批量清空队列。")]
    public async Task<CallToolResult> mpt_cancel(string item_id, CancellationToken cancellationToken = default)
    {
        return await access.Call(["cancel", "--item", item_id], cancellationToken);
    }
    [McpServerTool(Name = "mpt_cloud_accounts", ReadOnly = true, Destructive = false)]
    [Description("查看本机网盘账号、默认账号和中转策略，不返回账号凭据。")]
    public async Task<CallToolResult> mpt_cloud_accounts(CancellationToken cancellationToken = default)
    {
        return await access.Call(["cloud", "accounts"], cancellationToken);
    }
    [McpServerTool(Name = "mpt_pair_device", ReadOnly = false, Destructive = false)]
    [Description("从主机私密设备连接码文件配对一台设备，建立私聊权限。不会加入公屏。\n不要把连接码内容写进工具参数。")]
    public async Task<CallToolResult> mpt_pair_device(string invitation_file, CancellationToken cancellationToken = default)
    {
        return await access.Call(["pair", "--code-file", invitation_file], cancellationToken);
    }
    [McpServerTool(Name = "mpt_join_shared", ReadOnly = false, Destructive = false)]
    [Description("从私密文件助手会话邀请文件加入公屏。不同于设备私聊配对。")]
    public async Task<CallToolResult> mpt_join_shared(string invitation_file, CancellationToken cancellationToken = default)
    {
        return await access.Call(["join", "--code-file", invitation_file], cancellationToken);
    }
    [McpServerTool(Name = "mpt_cloud_select", ReadOnly = false, Destructive = false)]
    [Description("明确设置本机文件助手的默认网盘和持久中转策略，影响后续发送。\ncloud-only 不允许附件退回公网上传；auto 允许自动选择后端。")]
    public async Task<CallToolResult> mpt_cloud_select(string account_id, CloudMode mode = CloudMode.CloudOnly, CancellationToken cancellationToken = default)
    {
        var selected = await access.Call(["cloud", "default", "--account", account_id], cancellationToken);
        if (selected.IsError == true) return selected;
        return await access.Call(["cloud", "preferences", "--mode", Wire(mode)], cancellationToken);
    }
    [McpServerTool(Name = "mpt_cloud_authorize", ReadOnly = false, Destructive = false)]
    [Description("发起网盘授权并返回 operationId；已接入登录入口时才返回提供方网页。当前需要已有有效凭据文件。")]
    public async Task<CallToolResult> mpt_cloud_authorize(CloudProvider provider, CancellationToken cancellationToken = default)
    {
        return await access.Call(["cloud", "authorize", "--provider", Wire(provider)], cancellationToken);
    }
    [McpServerTool(Name = "mpt_cloud_complete", ReadOnly = false, Destructive = false)]
    [Description("用主机上已有的私密凭据文件完成授权，凭据存入平台凭据存储。\n不要把 cookie 或令牌内容写进工具参数；夸克用 cookie，百度用 refreshToken。")]
    public async Task<CallToolResult> mpt_cloud_complete(string operation_id, string credential_file, CredentialKind kind, CancellationToken cancellationToken = default)
    {
        return await access.Call(["cloud", "complete", "--operation", operation_id, "--credential-file", credential_file, "--kind", Wire(kind)], cancellationToken);
    }
}

[JsonConverter(typeof(StrictEnumConverter<RelayRoute>))]
public enum RelayRoute { [JsonStringEnumMemberName("auto")] Auto, [JsonStringEnumMemberName("quark")] Quark }
[JsonConverter(typeof(StrictEnumConverter<CloudMode>))]
public enum CloudMode { [JsonStringEnumMemberName("auto")] Auto, [JsonStringEnumMemberName("cloud-only")] CloudOnly }
[JsonConverter(typeof(StrictEnumConverter<CloudProvider>))]
public enum CloudProvider { [JsonStringEnumMemberName("quark")] Quark, [JsonStringEnumMemberName("baidu")] Baidu }
[JsonConverter(typeof(StrictEnumConverter<CredentialKind>))]
public enum CredentialKind { [JsonStringEnumMemberName("cookie")] Cookie, [JsonStringEnumMemberName("refreshToken")] RefreshToken }

public sealed class StrictEnumConverter<T>() : JsonStringEnumConverter<T>(allowIntegerValues: false) where T : struct, Enum;
