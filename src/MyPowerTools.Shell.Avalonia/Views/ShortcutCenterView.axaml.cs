using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class ShortcutCenterView : UserControl
{
    public ShortcutCenterView()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnRecordKey, RoutingStrategies.Tunnel, handledEventsToo: true);
        DetachedFromVisualTree += (_, _) => { if (DataContext is ShortcutCenterViewModel model) model.IsRecording = false; };
    }

    private void OnRecordClick(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ShortcutCenterViewModel model) return;
        model.IsRecording = !model.IsRecording;
        if (model.IsRecording) this.FindControl<Button>("RecordButton")?.Focus();
    }
    private void OnRecordKey(object? sender, KeyEventArgs args)
    {
        if (DataContext is not ShortcutCenterViewModel { IsRecording: true } model) return;
        args.Handled = true; // recording must never trigger a real command or insert its printable key
        var gesture = ShortcutKeyAdapter.Format(args.Key, args.KeyModifiers);
        if (gesture is not null) model.Record(ShortcutConfigurationClient.Display(gesture, model.EditorPlatform));
    }

    private async void OnImportClick(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ShortcutCenterViewModel model) return;
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage?.CanOpen != true) throw new InvalidOperationException("File picker is unavailable.");
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import keyboard shortcuts", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Shortcut configuration") { Patterns = ["*.json"] }]
            });
            if (files.Count == 0) return;
            using var file = files[0];
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            await model.ImportAsync(await reader.ReadToEndAsync());
        }
        catch (Exception ex) { model.ReportFileOperation($"Import failed: {ex.Message}"); }
    }
    private async void OnExportClick(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ShortcutCenterViewModel model) return;
        var json = model.Export();
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage?.CanSave != true) throw new InvalidOperationException("File picker is unavailable.");
            using var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export keyboard shortcuts", SuggestedFileName = "mypowertools-shortcuts.json", DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("Shortcut configuration") { Patterns = ["*.json"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(json); await writer.FlushAsync();
            model.ReportFileOperation("Exported user bindings for all platforms. Command arguments and credentials are not included.");
        }
        catch (Exception ex) { model.ReportFileOperation($"Export failed: {ex.Message}"); }
    }
}
