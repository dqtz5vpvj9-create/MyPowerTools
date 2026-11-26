using System.Text.Json;
using System.Text.Json.Nodes;

namespace FileTransfer.Core;

public sealed record BatchItemResult(string Path, string Name, bool Ok, bool Cancelled, string Message);

/// <summary>
/// Runs a multi-file send one file at a time. A failure is recorded and the remaining files are
/// still attempted, so one unreadable file cannot silently drop the rest of the selection.
/// </summary>
public static class BatchSend
{
    public const int MaxFiles = 1000;

    public static string Name(string path) => Path.GetFileName(path) is { Length: > 0 } name ? name : path;

    public static IReadOnlyList<string> Paths(JsonObject args)
    {
        var raw = args["paths"] ?? args["path"];
        if (raw is null) return [];
        var paths = new List<string>();
        switch (raw)
        {
            case JsonArray array:
                foreach (var node in array)
                {
                    if (node is null) continue;
                    var value = node.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(value)) paths.Add(value);
                }
                break;
            default:
                if (raw.GetValue<string>() is { Length: > 0 } single) paths.Add(single);
                break;
        }
        if (paths.Count > MaxFiles) throw new ArgumentException($"一次最多发送 {MaxFiles} 个文件。");
        foreach (var path in paths)
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择文件；路径必须是绝对路径。");
        return paths;
    }

    public static async Task<IReadOnlyList<BatchItemResult>> RunAsync(IReadOnlyList<string> paths,
        Func<string, CancellationToken, Task> send, CancellationToken token)
    {
        var results = new List<BatchItemResult>(paths.Count);
        foreach (var path in paths)
        {
            var name = Name(path);
            if (token.IsCancellationRequested)
            {
                results.Add(new(path, name, false, true, "已取消。"));
                continue;
            }
            try
            {
                await send(path, token);
                results.Add(new(path, name, true, false, ""));
            }
            catch (OperationCanceledException) { results.Add(new(path, name, false, true, "已取消。")); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                or System.Net.Sockets.SocketException or ArgumentException or JsonException or InvalidOperationException
                or NotSupportedException or System.Net.Http.HttpRequestException or FormatException)
            {
                results.Add(new(path, name, false, false, ex.Message));
            }
        }
        return results;
    }

    public static IReadOnlyList<BatchItemResult> Failed(IReadOnlyList<BatchItemResult> results) =>
        results.Where(item => !item.Ok).ToArray();

    public static string Summary(IReadOnlyList<BatchItemResult> results)
    {
        var done = results.Count(item => item.Ok);
        var cancelled = results.Count(item => item.Cancelled);
        var failed = results.Where(item => !item.Ok && !item.Cancelled).ToArray();
        var text = $"共 {results.Count} 个文件，成功 {done} 个";
        if (cancelled > 0) text += $"，已取消 {cancelled} 个";
        if (failed.Length > 0)
        {
            text += $"，失败 {failed.Length} 个：" + string.Join("；", failed.Take(3).Select(item => $"{item.Name}（{item.Message}）"));
            if (failed.Length > 3) text += " 等";
        }
        return text + "。";
    }
}
