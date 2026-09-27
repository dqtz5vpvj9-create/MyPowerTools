using Avalonia;
using Avalonia.Android;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Android;
using MyPowerTools.Shell.Avalonia;
using MyPowerTools.Shell.Avalonia.Views;
using A = global::Android;

namespace MyPowerTools.Android;

[A.App.Activity(Label = "MyPowerTools", MainLauncher = true, Exported = true, Theme = "@style/Theme.AppCompat.DayNight.NoActionBar",
    ConfigurationChanges = A.Content.PM.ConfigChanges.Orientation | A.Content.PM.ConfigChanges.ScreenSize | A.Content.PM.ConfigChanges.UiMode,
    // SingleTask keeps exactly one activity (and therefore one in-process runtime, one Shell and one
    // static ready task) in the task. With SingleTop a share arriving from Files could create a
    // second instance, overwrite the static Shell and orphan the activation that was still waiting
    // for the first one; every later intent is delivered through OnNewIntent instead.
    LaunchMode = A.Content.PM.LaunchMode.SingleTask)]
[A.App.IntentFilter([A.Content.Intent.ActionSend, A.Content.Intent.ActionSendMultiple], Categories = [A.Content.Intent.CategoryDefault], DataMimeType = "*/*")]
[A.App.IntentFilter([A.Content.Intent.ActionView], Categories = [A.Content.Intent.CategoryDefault, A.Content.Intent.CategoryBrowsable], DataScheme = "mpt")]
[A.App.IntentFilter([A.Content.Intent.ActionView], Categories = [A.Content.Intent.CategoryDefault, A.Content.Intent.CategoryBrowsable], DataScheme = "mypowertools")]
public sealed class MainActivity : AvaloniaMainActivity
{
    private static readonly object ShellGate = new();
    private static MobileShellView? _shell;
    private static TaskCompletionSource<MobileShellView> _shellReady = NewShellReady();
    private static TaskCompletionSource<MobileShellView> NewShellReady()
    {
        var completion = new TaskCompletionSource<MobileShellView>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A failed launch leaves this task faulted; observe it so intents report the failure instead
        // of the process carrying an unobserved exception until the retry succeeds.
        _ = completion.Task.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
        return completion;
    }

    /// <summary>
    /// The completion waiting activations observe. It is replaced only when it can no longer be
    /// satisfied - a Shell is already live (the activity was recreated) or the previous attempt
    /// failed - so two files shared while the first Shell is still being built wait on the same
    /// instance instead of one of them waiting forever on an orphaned task.
    /// </summary>
    private static TaskCompletionSource<MobileShellView> ArmShellReady()
    {
        lock (ShellGate)
        {
            if (_shellReady.Task.IsCompleted)
            {
                _shellReady = NewShellReady();
            }

            return _shellReady;
        }
    }

    internal static Avalonia.Controls.Control CreateMainView()
    {
        ArmShellReady();
        var view = new AndroidStartupView();
        _ = InitializeAsync(view);
        return view;
    }

    private static async Task InitializeAsync(AndroidStartupView view)
    {
        var ready = ArmShellReady();
        try
        {
            await AndroidHost.InitializeAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AndroidStartupLog.Info("shell", "Building the touch Shell view");
                _shell = new MobileShellView();
                view.ShowShell(_shell);
                ready.TrySetResult(_shell);
            });
        }
        catch (Exception ex)
        {
            AndroidStartupLog.Error("startup", ex);
            ready.TrySetException(ex);
            await Dispatcher.UIThread.InvokeAsync(() => view.ShowFailure(ex, () =>
            {
                AndroidStartupLog.Info("retry", "User requested a startup retry");
                ArmShellReady();
                _ = InitializeAsync(view);
            }));
        }
    }

    protected override void OnCreate(A.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        OnBackPressedDispatcher.AddCallback(this, new BackCallback(this));
        AndroidBackgroundActivityService.NotificationPermissionRequest = RequestNotificationPermissionAsync;
        if (Intent is { } intent) _ = HandleIntentAsync(intent);
    }

    private TaskCompletionSource<bool>? _notificationPermission;
    private Task<bool> RequestNotificationPermissionAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || CheckSelfPermission(A.Manifest.Permission.PostNotifications) == A.Content.PM.Permission.Granted)
            return Task.FromResult(true);
        if (_notificationPermission is not null) return _notificationPermission.Task;
        _notificationPermission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() => RequestPermissions([A.Manifest.Permission.PostNotifications], 47));
        return _notificationPermission.Task;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, A.Content.PM.Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != 47) return;
        var completion = _notificationPermission;
        _notificationPermission = null;
        completion?.TrySetResult(grantResults.Length > 0 && grantResults[0] == A.Content.PM.Permission.Granted);
    }

    private sealed class BackCallback(MainActivity activity) : global::AndroidX.Activity.OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed() => _ = activity.HandleBackAsync();
    }

    private async Task HandleBackAsync()
    {
        try
        {
            var shell = _shell;
            if (shell is not null && await shell.HandleBackAsync()) return;
        }
        catch (Exception ex)
        {
            AndroidStartupLog.Error("back", ex);
        }

        MoveTaskToBack(true);
    }

    protected override void OnNewIntent(A.Content.Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is not null) _ = HandleIntentAsync(intent);
    }

    protected override void OnDestroy()
    {
        // The activity (and its visual tree) is gone; the next one arms a fresh ready task instead
        // of handing activations a control that no longer has a parent.
        _shell = null;
        base.OnDestroy();
    }

    /// <summary>
    /// Reads the shared content from either share action. Apps send <c>EXTRA_STREAM</c> as an
    /// <c>ArrayList&lt;Uri&gt;</c>, a <c>String[]</c> or an <c>ArrayList&lt;String&gt;</c>, so all
    /// three shapes are accepted instead of dropping a share the system allowed through.
    /// </summary>
    private static A.Net.Uri[] ReadSharedUris(A.Content.Intent intent)
    {
        var uris = new List<A.Net.Uri>();
#pragma warning disable CA1422 // The legacy overload supports API 29 through current Android.
        if (intent.Action == A.Content.Intent.ActionSendMultiple)
        {
            if (intent.GetParcelableArrayListExtra(A.Content.Intent.ExtraStream) is { } parcelables)
            {
                uris.AddRange(parcelables.OfType<A.Net.Uri>());
            }

            if (uris.Count == 0 && intent.GetStringArrayListExtra(A.Content.Intent.ExtraStream) is { } strings)
            {
                uris.AddRange(ParseUris(strings));
            }

            if (uris.Count == 0 && intent.GetStringArrayExtra(A.Content.Intent.ExtraStream) is { } array)
            {
                uris.AddRange(ParseUris(array));
            }
        }
        else
        {
            if (intent.GetParcelableExtra(A.Content.Intent.ExtraStream) is A.Net.Uri single)
            {
                uris.Add(single);
            }
            else if (intent.GetStringExtra(A.Content.Intent.ExtraStream) is { } value)
            {
                uris.AddRange(ParseUris([value]));
            }
        }
#pragma warning restore CA1422
        return uris.ToArray();
    }

    private static IEnumerable<A.Net.Uri> ParseUris(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (A.Net.Uri.Parse(value) is { } uri) yield return uri;
        }
    }

    private readonly SemaphoreSlim _intentGate = new(1);
    private async Task HandleIntentAsync(A.Content.Intent intent)
    {
        await _intentGate.WaitAsync();
        try
        {
            await AndroidHost.InitializeAsync();
            var activations = new List<string>();
            IReadOnlyList<(string Id, string Title)> targets;
            if (intent.Action == A.Content.Intent.ActionView && intent.DataString is { } link)
            {
                AndroidStartupLog.Info("intent", "Received a tool link");
                targets = AndroidHost.ActivationTargets(link);
                activations.Add(link);
            }
            else if (intent.Action is A.Content.Intent.ActionSend or A.Content.Intent.ActionSendMultiple)
            {
                var uris = ReadSharedUris(intent);
                if (uris.Length == 0) return;
                AndroidStartupLog.Info("intent", $"Share of {uris.Length} file(s) as {intent.Type}");
                targets = AndroidHost.ShareTargets(intent.Type ?? "application/octet-stream");
                foreach (var uri in uris)
                {
                    var name = uri.Scheme == "file" ? uri.LastPathSegment ?? "shared-file" : "shared-file";
                    using (var cursor = ContentResolver!.Query(uri, [A.Provider.IOpenableColumns.DisplayName], null, null, null))
                        if (cursor?.MoveToFirst() == true) name = cursor.GetString(0) ?? name;
                    name = Path.GetFileName(name.Replace('\\', '/'));
                    var folder = Path.Combine(CacheDir!.AbsolutePath, "shares", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, name);
                    await using (var input = ContentResolver!.OpenInputStream(uri) ?? throw new IOException("无法读取分享的文件。"))
                    await using (var output = File.Create(path)) await input.CopyToAsync(output);
                    activations.Add(new Uri(path).AbsoluteUri);
                }
            }
            else return;
            if (targets.Count == 0) throw new InvalidOperationException("尚未启用支持此内容的工具。");
            async Task OpenAsync(string toolId)
            {
                // Wait for the Shell that actually owns the runtime, not for a timer: a share that
                // arrives during startup must land on the same instance the UI shows.
                var shell = await _shellReady.Task;
                AndroidStartupLog.Info("intent", $"Activating {toolId} with {activations.Count} activation(s)");
                foreach (var activation in activations)
                    await shell.ActivateAsync(new ToolActivationRequest(toolId, "", activation));
            }
            if (targets.Count == 1) await Dispatcher.UIThread.InvokeAsync(() => OpenAsync(targets[0].Id));
            else RunOnUiThread(() =>
            {
                using var dialog = new A.App.AlertDialog.Builder(this);
                dialog.SetTitle("使用哪个工具打开？");
                dialog.SetItems(targets.Select(t => t.Title).ToArray(), async (_, args) => await Dispatcher.UIThread.InvokeAsync(() => OpenAsync(targets[args.Which].Id)));
                dialog.Show();
            });
        }
        catch (Exception ex)
        {
            AndroidStartupLog.Error("intent", ex);
            RunOnUiThread(() => A.Widget.Toast.MakeText(this, ex.Message, A.Widget.ToastLength.Long)?.Show());
        }
        finally { _intentGate.Release(); }
    }
}
