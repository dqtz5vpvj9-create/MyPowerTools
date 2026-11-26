namespace MyPowerTools.Shell.Avalonia.Services.Mobile;

/// <summary>Task results are presented separately from tool availability and runtime state.</summary>
public static class MobileStatusText
{
    public static string TransferState(string state) => state.ToLowerInvariant() switch
    {
        "active" or "started" or "running" => "传输中",
        "sending" or "uploading" => "发送中",
        "downloading" or "receiving" => "接收中",
        "queued" or "pending" => "等待发送",
        "waiting-network" => "等待网络",
        "retrying" => "等待重试",
        "uploaded" or "relay-uploaded" or "stored" => "已暂存，等待对方接收",
        "delivered" => "已送达",
        "received" => "已接收",
        "saved" or "local" => "已保存到本机",
        "completed" or "succeeded" => "已完成",
        "cancelled" => "已取消",
        "failed" or "error" => "传输失败",
        _ => "状态暂不可用"
    };

    public static string TransferDirection(string direction) => direction.ToLowerInvariant() switch
    {
        "send" => "发送",
        "receive" => "接收",
        _ => "传输"
    };
}
