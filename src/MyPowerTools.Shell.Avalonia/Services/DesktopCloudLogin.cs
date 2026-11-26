using System.Diagnostics;
using System.Text.Json;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.WebSurface.Avalonia;

namespace MyPowerTools.Shell.Avalonia.Services;

internal static class DesktopCloudLogin
{
    internal static async Task<MptCloudAuthorizationResult?> AuthorizeAsync(string provider, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || provider is not ("quark" or "baidu"))
            throw new InvalidOperationException("当前平台尚未接入此网盘登录。");
        var host = AvaloniaWebSurfaceService.ResolveDefaultHostPath(AppContext.BaseDirectory);
        if (!File.Exists(host)) throw new InvalidOperationException("网盘登录组件缺失，请更新开发版后重试。");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        start.ArgumentList.Add("--cloud-login"); start.ArgumentList.Add(provider);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法打开网盘登录。");
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(token);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new InvalidOperationException("网盘登录窗口未正常完成，请重试。");
            if (line is null or "null") return null;
            var result = JsonSerializer.Deserialize<MptCloudAuthorizationResult>(line);
            if (result?.ProviderId != provider || result.CredentialKind != (provider == "quark" ? "cookie" : "refreshToken"))
                throw new InvalidOperationException("网盘登录结果不匹配，请重试。");
            return result;
        }
        catch (JsonException) { throw new InvalidOperationException("网盘登录返回无效，请重试。"); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
