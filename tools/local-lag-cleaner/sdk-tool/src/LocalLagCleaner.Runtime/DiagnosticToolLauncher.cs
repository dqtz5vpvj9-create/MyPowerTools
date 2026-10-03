using System.Diagnostics;
using System.Text.Json.Nodes;

namespace LocalLagCleaner.Runtime;

internal static class DiagnosticToolLauncher
{
    // Fixed targets only. The JSON-RPC commands accept no paths or arguments.
    public static JsonObject Open(string commandId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("诊断入口仅支持 Windows。");
        var (target, argument, label, uri) = commandId switch
        {
            "local-lag-cleaner.open.resource-monitor" => ("resmon.exe", "", "资源监视器", false),
            "local-lag-cleaner.open.task-manager" => ("Taskmgr.exe", "", "任务管理器", false),
            "local-lag-cleaner.open.storage" => ("ms-settings:storagesense", "", "存储设置", true),
            "local-lag-cleaner.open.startup" => ("ms-settings:startupapps", "", "启动应用", true),
            "local-lag-cleaner.open.update" => ("ms-settings:windowsupdate", "", "Windows 更新", true),
            "local-lag-cleaner.open.devices" => ("mmc.exe", "devmgmt.msc", "设备管理器", false),
            "local-lag-cleaner.open.reliability" => ("perfmon.exe", "/rel", "可靠性历史", false),
            _ => throw new ArgumentException("未定义的诊断入口。", nameof(commandId))
        };
        var start = new ProcessStartInfo
        {
            FileName = uri ? target : Path.Combine(Environment.SystemDirectory, target),
            UseShellExecute = uri,
            CreateNoWindow = true
        };
        if (argument.Length > 0)
            start.ArgumentList.Add(argument == "devmgmt.msc"
                ? Path.Combine(Environment.SystemDirectory, argument) : argument);
        using var process = Process.Start(start);
        return new JsonObject { ["message"] = $"已请求打开{label}。按方案完成处理后，点击“深度复测”检查效果。" };
    }
}
