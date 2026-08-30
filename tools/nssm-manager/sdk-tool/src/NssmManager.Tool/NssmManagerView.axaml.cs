using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace NssmManager.Tool;

public partial class NssmManagerView : UserControl, IMptAvaloniaSurfaceActivationHandler
{
    public NssmManagerView() => InitializeComponent();

    private async void BrowseApplication_Click(object? sender, RoutedEventArgs e) => await BrowseAsync(NssmBrowseTarget.Application, applicationFilter: true);
    private async void BrowseDirectory_Click(object? sender, RoutedEventArgs e) => await BrowseAsync(NssmBrowseTarget.Directory);
    private async void BrowseStdin_Click(object? sender, RoutedEventArgs e) => await BrowseAsync(NssmBrowseTarget.Stdin);
    private async void BrowseStdout_Click(object? sender, RoutedEventArgs e) => await BrowseAsync(NssmBrowseTarget.Stdout);
    private async void BrowseStderr_Click(object? sender, RoutedEventArgs e) => await BrowseAsync(NssmBrowseTarget.Stderr);
    private async void BrowseHook_Click(object? sender, RoutedEventArgs e) => await BrowseAsync(NssmBrowseTarget.Hook);

    private void AffinityAll_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is NssmManagerViewModel model && sender is CheckBox box) model.set_affinity_enabled(box.IsChecked != true);
    }

    private void HookCommand_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NssmManagerViewModel model) return;
        model.set_hook_tab(model.HookEvents.ToList().IndexOf(model.SelectedHookEvent), model.AvailableHookActions.ToList().IndexOf(model.SelectedHookAction), true);
    }

    private async Task BrowseAsync(NssmBrowseTarget target, bool applicationFilter = false)
    {
        if (DataContext is not NssmManagerViewModel model || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        string? selected;
        if (target == NssmBrowseTarget.Directory)
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false, Title = "选择工作目录" });
            selected = folders.FirstOrDefault()?.TryGetLocalPath();
        }
        else
        {
            var types = applicationFilter
                ? new[] { new FilePickerFileType("应用程序") { Patterns = ["*.exe", "*.bat", "*.cmd"] }, FilePickerFileTypes.All }
                : new[] { FilePickerFileTypes.All };
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, Title = "选择文件", FileTypeFilter = types });
            selected = files.FirstOrDefault()?.TryGetLocalPath();
        }
        if (!string.IsNullOrWhiteSpace(selected)) model.browse(target, selected);
    }

    public async ValueTask<bool> ActivateAsync(ToolActivationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DataContext is not NssmManagerViewModel model) return false;
        if (!Uri.TryCreate(request.ActivationUri, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("nssm-manager", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("open", StringComparison.OrdinalIgnoreCase)) return false;

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("mode", out var mode)) return false;
        query.TryGetValue("service", out var serviceName);
        var activation = await Dispatcher.UIThread.InvokeAsync(
            () => model.ActivateAsync(mode, serviceName, cancellationToken),
            DispatcherPriority.Normal,
            cancellationToken);
        return await activation.ConfigureAwait(false);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = component.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]);
            var value = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : string.Empty;
            result[key] = value;
        }
        return result;
    }
}
