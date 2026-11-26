using System.Text.Json.Nodes;
using Avalonia.Controls;
using MyPowerTools.AvaloniaSdk.Controls;

namespace FileTransfer.Surface;

internal sealed partial class AssistantView
{
    private async Task ShowStorageAsync()
    {
        var result = await _core.InspectStorageAsync();
        var files = (result["files"] as JsonArray ?? []).Select(node => new
        {
            Id = node!["itemId"]!.GetValue<string>(),
            Name = node["name"]!.GetValue<string>(),
            Bytes = node["bytes"]!.GetValue<long>()
        }).ToArray();
        _sheetTitle.Text = "空间管理";
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(MobileUi.Body($"已下载 {files.Length} 个文件 · {StorageSize(files.Sum(file => file.Bytes))}"));
        body.Children.Add(MobileUi.Caption("大于 15 MB 的附件需点按下载。清理仅移除助手内的下载副本，保留聊天记录。另存到系统下载目录的文件不受影响。再次点按可重新下载，需来源仍可用。"));
        var selected = new HashSet<string>();
        var clean = MobileUi.PrimaryButton("选择要清理的文件");
        clean.IsEnabled = false;
        foreach (var file in files)
        {
            var check = new CheckBox { Content = MobileUi.Stack(4, MobileUi.Body(file.Name), MobileUi.Caption(StorageSize(file.Bytes))), MinHeight = 48 };
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) selected.Add(file.Id); else selected.Remove(file.Id);
                clean.IsEnabled = selected.Count > 0;
                clean.Content = $"清理 {selected.Count} 个文件 · {StorageSize(files.Where(f => selected.Contains(f.Id)).Sum(f => f.Bytes))}";
            };
            body.Children.Add(check);
        }
        clean.Click += (_, _) =>
        {
            var ids = selected.ToArray();
            var confirm = MobileUi.PrimaryButton("确认清理本机副本");
            confirm.Click += async (_, _) => await RunAsync(async () =>
            {
                confirm.IsEnabled = false;
                var answer = await _core.CleanStorageAsync(ids);
                var errors = (answer["errors"] as JsonArray ?? []).Select(error => error!.GetValue<string>());
                _sheetScroll.Content = MobileUi.Stack(12,
                    MobileUi.Body($"已释放 {StorageSize(answer["freedBytes"]!.GetValue<long>())}"),
                    MobileUi.Caption(string.Join("\n", errors)),
                    MobileUi.Note("聊天记录仍保留。文件不会自动下载回来。"));
            });
            _sheetScroll.Content = MobileUi.Stack(12, MobileUi.Body($"清理选中的 {ids.Length} 个文件？"),
                MobileUi.Caption("只删除这台设备的下载副本，不删除其他设备或网盘上的内容。"), confirm);
        };
        body.Children.Add(clean);
        _sheetScroll.Content = body;
        OpenSheet();
    }

    private static string StorageSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024):0.#} MB" : $"{bytes / 1024d:0.#} KB";
}
