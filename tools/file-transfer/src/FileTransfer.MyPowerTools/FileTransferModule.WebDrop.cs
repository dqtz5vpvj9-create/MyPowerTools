using System.Text.Json.Nodes;
using FileTransfer.Core.WebDrop;
using MyPowerTools.Abstractions;

namespace FileTransfer.MyPowerTools;

public sealed partial class FileTransferModule
{
    private readonly object _webDropLock = new();
    private WebDropServer? _webDrop;

    internal Task<(bool Handled, object? Result)> TryExecuteWebDropCommandAsync(
        string commandId,
        JsonObject args,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        switch (commandId)
        {
            case "file-transfer.drop.create":
                return Task.FromResult<(bool, object?)>((true, CreateWebDrop(args)));

            case "file-transfer.drop.list":
                return Task.FromResult<(bool, object?)>((true, ListWebDrops(args)));

            case "file-transfer.drop.revoke":
                return Task.FromResult<(bool, object?)>((true, RevokeWebDrop(args)));

            default:
                return Task.FromResult<(bool, object?)>((false, null));
        }
    }

    internal async ValueTask DisposeWebDropAsync()
    {
        WebDropServer? server;
        lock (_webDropLock)
        {
            server = _webDrop;
            _webDrop = null;
        }

        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private WebDropServer EnsureWebDropServer()
    {
        lock (_webDropLock)
        {
            _webDrop ??= new WebDropServer();
            return _webDrop;
        }
    }

    private WebDropInfo CreateWebDrop(JsonObject args)
    {
        var rawPath = SettingsJson.ReadString(args, "path")
            ?? SettingsJson.ReadString(args, "file");
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new ArgumentException("缺少要共享的文件路径（path）。");
        }

        var fullPath = Path.GetFullPath(rawPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("要共享的文件不存在。", fullPath);
        }

        var ttlSeconds = SettingsJson.ReadInt(args, "ttlSeconds")
            ?? SettingsJson.ReadInt(args, "ttl")
            ?? (int)WebDropDefaults.DefaultTtl.TotalSeconds;
        var ttl = TimeSpan.FromSeconds(ttlSeconds);
        if (ttl < WebDropDefaults.MinTtl || ttl > WebDropDefaults.MaxTtl)
        {
            throw new ArgumentException(
                $"有效期 ttlSeconds 必须在 {(int)WebDropDefaults.MinTtl.TotalSeconds} 到 {(int)WebDropDefaults.MaxTtl.TotalSeconds} 秒之间。");
        }

        var maxDownloads = SettingsJson.ReadInt(args, "maxDownloads") ?? 0;
        if (maxDownloads < 0)
        {
            throw new ArgumentException("maxDownloads 不能为负数（0 表示过期前不限次数）。");
        }

        var server = EnsureWebDropServer();
        return server.CreateDrop(fullPath, ttl, maxDownloads);
    }

    private object ListWebDrops(JsonObject args)
    {
        var includeInactive = args["includeInactive"]?.GetValue<bool>() == true;
        WebDropServer? server;
        lock (_webDropLock)
        {
            server = _webDrop;
        }

        if (server is null)
        {
            return new
            {
                listening = false,
                port = 0,
                drops = Array.Empty<WebDropInfo>()
            };
        }

        return new
        {
            listening = server.IsListening,
            port = server.Port,
            drops = server.ListDrops(includeInactive)
        };
    }

    private object RevokeWebDrop(JsonObject args)
    {
        var dropId = (SettingsJson.ReadString(args, "id")
            ?? SettingsJson.ReadString(args, "dropId")
            ?? "").Trim();
        if (string.IsNullOrEmpty(dropId))
        {
            throw new ArgumentException("缺少要撤销的链接编号（id）。");
        }

        WebDropServer? server;
        lock (_webDropLock)
        {
            server = _webDrop;
        }

        var revoked = server?.RevokeDrop(dropId) == true;
        return new
        {
            id = dropId,
            revoked
        };
    }
}
