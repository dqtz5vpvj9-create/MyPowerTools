using Android.Content.Res;
using Android.Views;
using Android.Views.InputMethods;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.Android.Files;
using MyPowerTools.Android.Pairing;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Android;
using MyPowerTools.Shell.Avalonia;
using MyPowerTools.Shell.Avalonia.Views;
using A = global::Android;

namespace MyPowerTools.Android;

[A.App.Activity(Label = "MyPowerTools", MainLauncher = true, Exported = true, Theme = "@style/Theme.MyPowerTools",
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
    /// The live activity, for host features that must start another activity (the pairing scanner).
    /// It is cleared in <see cref="OnDestroy"/>, so a scan requested while no window exists reports
    /// "not ready" instead of starting from a destroyed context.
    /// </summary>
    internal static MainActivity? Current { get; private set; }

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
        WarmFontStack();
        var view = new AndroidStartupView();
        _ = InitializeAsync(view);
        return view;
    }

    /// <summary>
    /// Resolves the fonts the first frame needs, on the UI thread and before that frame is laid out.
    /// <para>
    /// Identical cold starts of this APK on the MPT AVD either render every glyph or render
    /// regular-weight Han (and the embedded icon font) as empty boxes for the whole process, while
    /// bold runs stay correct — the same screen, the same process, three cold starts in a row
    /// produced one bad and two good. That signature means the first typeface/glyph lookup of a
    /// process can race the font stack's initialization and the miss is cached for the rest of the
    /// process. Laying out a throwaway probe here moves that first lookup to a point where the
    /// platform is fully up, and records what actually resolved so a bad start names its cause
    /// instead of only showing boxes.
    /// </para>
    /// </summary>
    private static void WarmFontStack()
    {
        try
        {
            var manager = FontManager.Current;
            var resolved = new List<string>();
            var missing = new List<string>();
            foreach (var family in new[] { "sans-serif", "monospace" })
            {
                if (manager.TryGetGlyphTypeface(new Typeface(new FontFamily(family)), out var glyph) && glyph is not null)
                {
                    resolved.Add(family);
                }
                else
                {
                    missing.Add(family);
                }
            }

            // Deliberately exercise the Han path every surface depends on and record what it chose:
            // a start that resolves no Han face is the one that would draw boxes.
            var hanMatched = manager.TryMatchCharacter(
                '正', FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, null, null, out var han);

            var probe = new TextBlock { Text = "正在加载工具与设备", FontSize = 14 };
            probe.Measure(Size.Infinity);

            AndroidStartupLog.Info("fonts",
                $"warm-up resolved=[{string.Join(',', resolved)}] missing=[{string.Join(',', missing)}] " +
                $"hanMatched={hanMatched} han={(hanMatched ? han.FontFamily.Name : "(none)")} " +
                $"probeWidth={probe.DesiredSize.Width:F1} default={manager.DefaultFontFamily.Name}");
        }
        catch (Exception ex)
        {
            // Diagnostics must never stop a launch.
            AndroidStartupLog.Error("fonts", ex);
        }
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
                var shell = new MobileShellView();
                // Native surface capabilities are connected before any activation can reach a tool
                // surface: the camera scanner and the platform file opener are Android-owned, and a
                // surface that loads without them simply cannot scan or open a local file.
                shell.SetNativeSurfaceServices(ScanConnectionCodeAsync, OpenLocalFileAsync);
                _shell = shell;
                view.ShowShell(shell);
                ready.TrySetResult(shell);
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
        Current = this;
        base.OnCreate(savedInstanceState);
        ApplySystemBars();
        // The LAN discovery module runs in this process and takes a multicast lease only while a
        // discovery window (or an enabled receiver) is actually listening. Publishing the factory here
        // is the single wiring point; nothing is acquired at startup, and the module never references
        // this assembly.
        MobileWifiMulticast.Current ??= AndroidWifiMulticast.TryCreate();
        OnBackPressedDispatcher.AddCallback(this, new BackCallback(this));
        AndroidBackgroundActivityService.NotificationPermissionRequest = RequestNotificationPermissionAsync;
        if (Intent is { } intent) _ = HandleIntentAsync(intent);
    }

    /// <summary>
    /// Keeps the system bars on the mobile page colour <em>and</em> their icons on the matching
    /// contrast. The window theme already does this for the first frame; a system night-mode switch
    /// arrives here because the activity handles <c>uiMode</c> itself, and an activity that kept the
    /// launch values would show dark text on a dark bar (or the reverse) until it was recreated.
    /// Called after <c>base.OnCreate</c> so the window exists.
    /// </summary>
    private void ApplySystemBars()
    {
        try
        {
            var window = Window;
            if (window is null) return;

            // The effective night mode is the one the window theme used, so the icons can never
            // disagree with the colour chosen here. AppCompat owns that value; -1 (follow system)
            // falls back to the current configuration.
            // AppCompatDelegate.DefaultNightMode is the AppCompat int constant, not the UiMode enum.
            var nightMode = AppCompatDelegate.DefaultNightMode;
            var night = nightMode == (int)UiMode.NightYes ||
                        (nightMode != (int)UiMode.NightNo &&
                         (Resources?.Configuration?.UiMode & UiMode.NightMask) == UiMode.NightYes);
            var page = night ? "#171B22" : "#F6F7F9";
            // Deprecated on API 35+, where the system draws edge to edge and the bar takes the colour
            // of what is behind it - which is this app's page background. The call is what keeps
            // API 29..34 on the same colour, so it stays with an explicit acknowledgement.
#pragma warning disable CA1422
            window.SetStatusBarColor(A.Graphics.Color.ParseColor(page));
            window.SetNavigationBarColor(A.Graphics.Color.ParseColor(page));
#pragma warning restore CA1422

            if (window.DecorView is not { } decor) return;
            var controller = WindowCompat.GetInsetsController(window, decor);
            if (controller is null) return;

            // AppearanceLightStatusBars == true means dark icons, which is the light appearance.
            controller.AppearanceLightStatusBars = !night;
            controller.AppearanceLightNavigationBars = !night;
        }
        catch (Exception ex)
        {
            // Appearance is not worth failing a launch over.
            AndroidStartupLog.Error("system-bars", ex);
        }
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplySystemBars();
    }

    /// <summary>
    /// The camera scan a tool surface can request. It resolves to the classified
    /// <c>mpt://</c> link, or to <see langword="null"/> when the user cancelled or refused the camera
    /// permission; the credential never passes through a log or an intermediate store. The Shell may
    /// call this from a surface callback, so the launch is marshalled to the UI thread.
    /// </summary>
    private static async Task<string?> ScanConnectionCodeAsync(CancellationToken cancellationToken)
    {
        var result = await Dispatcher.UIThread.InvokeAsync(
            () => MobileQrScan.Default.ScanAsync(cancellationToken)).ConfigureAwait(true);
        return result.Code?.Value;
    }

    /// <summary>
    /// Opens a local file with the platform viewer. The Android implementation owns the URI grant and
    /// the shared-root policy; the Shell only forwards the path.
    /// <para>
    /// The launcher reports a refusal (missing file, path outside the shared roots, no viewer) as
    /// <see cref="MptFileOpenException"/> with a message written for the user, but the shared
    /// surface's open strategy only turns the exception types it knows into a status line. Converting
    /// it here keeps the real reason in front of the user instead of letting it escape as an
    /// unhandled exception inside a page callback; a typed refusal in the Shell contract would remove
    /// the need for this adapter.
    /// </para>
    /// </summary>
    private static async Task<bool> OpenLocalFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await AndroidFileLauncher.OpenAsync(path, cancellationToken).ConfigureAwait(true);
        }
        catch (MptFileOpenException ex)
        {
            AndroidStartupLog.Write("warn", "file-open", "打开被拒绝：" + ex.Failure);
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    private TaskCompletionSource<bool>? _notificationPermission;
    private Task<bool> RequestNotificationPermissionAsync()
    {
        // The permission itself only exists from API 33, so the version check is not just an
        // optimisation: the platform analyser needs it to be a separate branch.
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return Task.FromResult(true);
        // Everything below this line runs on API 33+, which is the only place the permission exists.
        // The analyser does not follow the early return, so the two call sites are acknowledged.
#pragma warning disable CA1416
        if (CheckSelfPermission(A.Manifest.Permission.PostNotifications) == A.Content.PM.Permission.Granted)
            return Task.FromResult(true);
        if (_notificationPermission is not null) return _notificationPermission.Task;
        _notificationPermission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(() => RequestPermissions([A.Manifest.Permission.PostNotifications], 47));
#pragma warning restore CA1416
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
        // An open keyboard is the topmost layer, so back closes it first. Without this the gesture
        // left the tool page (or the app) while the keyboard was still covering the screen.
        try
        {
            if (IsSoftKeyboardVisible())
            {
                (GetSystemService(InputMethodService) as InputMethodManager)
                    ?.HideSoftInputFromWindow(Window?.DecorView?.WindowToken, 0);
                return;
            }
        }
        catch (Exception ex)
        {
            AndroidStartupLog.Error("back-keyboard", ex);
        }

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

    /// <summary>
    /// The IME inset is the only reliable signal for "a keyboard is on screen" (the window is
    /// edge-to-edge, so the legacy window-metrics comparison reports a keyboard that is not there).
    /// This uses the AndroidX compat API on purpose: <c>getRootWindowInsets().getInsets(int)</c> and
    /// <c>WindowInsets.Type</c> are API 30, while the app supports API 29.
    /// </summary>
    private bool IsSoftKeyboardVisible()
    {
        if (Window?.DecorView is not { } decor) return false;
        if (ViewCompat.GetRootWindowInsets(decor) is not { } insets) return false;
        if (insets.GetInsets(WindowInsetsCompat.Type.Ime()!) is not { } ime) return false;
        if (insets.GetInsets(WindowInsetsCompat.Type.SystemBars()!) is not { } systemBars) return false;
        return ime.Bottom > systemBars.Bottom;
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
        if (ReferenceEquals(Current, this)) Current = null;
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

    /// <summary>
    /// The text of a share. Plain text shares (a link, a note, a selected paragraph) carry
    /// <c>EXTRA_TEXT</c> and no stream at all, which the previous host dropped before it ever reached
    /// the file assistant. <c>EXTRA_HTML_TEXT</c> and the intent's <c>ClipData</c> are read as
    /// fallbacks because that is where several messengers and browsers put the selection.
    /// </summary>
    private static string? ReadSharedText(A.Content.Intent intent)
    {
        var text = ReadExtraText(intent, A.Content.Intent.ExtraText)
                   ?? ReadExtraText(intent, A.Content.Intent.ExtraHtmlText);
        if (!string.IsNullOrWhiteSpace(text)) return text;

        var clip = intent.ClipData;
        if (clip is null) return null;
        for (var index = 0; index < clip.ItemCount; index++)
        {
            var item = clip.GetItemAt(index);
            if (item?.Text is { Length: > 0 } itemText && !string.IsNullOrWhiteSpace(itemText)) return itemText;
            if (item?.HtmlText is { Length: > 0 } html && !string.IsNullOrWhiteSpace(html)) return html;
        }

        return null;
    }

    private static string? ReadExtraText(A.Content.Intent intent, string name)
    {
#pragma warning disable CA1422 // The legacy overload supports API 29 through current Android.
        try
        {
            return intent.GetCharSequenceExtra(name)?.ToString();
        }
        catch (Exception)
        {
            // A misbehaving sender can put a non-CharSequence in the extra; a share must not crash
            // the host because of it.
            return null;
        }
#pragma warning restore CA1422
    }

    // Static on purpose: the activity can be recreated between two shares, and two intents must
    // still be ordered against each other and against the single Shell they both activate.
    private static readonly SemaphoreSlim IntentGate = new(1);

    private async Task HandleIntentAsync(A.Content.Intent intent)
    {
        await IntentGate.WaitAsync();
        try
        {
            await AndroidHost.InitializeAsync();
            var activations = new List<string>();
            IReadOnlyList<(string Id, string Title)> targets;
            // The assistant is the built-in entry point for what a share can carry: shared text and
            // shared files both belong to its conversation, which is the user's daily action. Picking
            // a tool stays possible, but never as a required step before sending.
            var assistant = AndroidHost.AssistantToolFor(ShareActivation.AssistantUriPrefix) ?? AndroidHost.AssistantToolFor(ShareActivation.AssistantLinkPrefix);
            var chooseTool = false;
            if (intent.Action == A.Content.Intent.ActionView && intent.DataString is { } link)
            {
                // A connection code is a receiver secret, so only a secret-free summary is logged.
                // The shared classifier owns the redaction rule; an unrecognised link is never
                // echoed either.
                var described = MobileQrPayload.TryClassify(link, out var classified, out _)
                    ? classified!.Describe()
                    : "未识别的链接（内容已隐藏）";
                AndroidStartupLog.Info("intent", "Received a tool link: " + described);
                targets = AndroidHost.ActivationTargets(link);
                activations.Add(link);
            }
            else if (intent.Action is A.Content.Intent.ActionSend or A.Content.Intent.ActionSendMultiple)
            {
                var text = ReadSharedText(intent);
                var uris = ReadSharedUris(intent);
                if (ShareActivation.IsEmpty(text, uris.Length)) return;

                // Whitespace-only text is not content: with attachments it keeps the declared
                // destinations and the chooser instead of being treated as a text share.
                var hasText = !string.IsNullOrWhiteSpace(text);
                // The summary never includes the shared text: it can be a password or a message.
                AndroidStartupLog.Info("intent", "Share received: " + ShareActivation.Describe(
                    hasText, uris.Length, intent.Type));

                if (hasText && assistant is { } textOwner)
                {
                    targets = [textOwner];
                }
                else
                {
                    // Attachment shares keep the declared destinations, so an explicitly installed
                    // tool can still be chosen; the assistant is the default when none declares the
                    // type.
                    targets = AndroidHost.ShareTargets(intent.Type ?? "application/octet-stream");
                    if (targets.Count == 0 && assistant is { } fileOwner) targets = [fileOwner];
                    // Several declared tools for this type: keep the chooser so an explicit
                    // destination stays reachable. The assistant is listed like any other candidate.
                    chooseTool = targets.Count > 1;
                }

                var sharesDirectory = Path.Combine(CacheDir!.AbsolutePath, "shares");
                var attachmentUris = new List<string>(uris.Length);
                foreach (var uri in uris)
                {
                    var name = uri.Scheme == "file" ? uri.LastPathSegment ?? "shared-file" : "shared-file";
                    using (var cursor = ContentResolver!.Query(uri, [A.Provider.IOpenableColumns.DisplayName], null, null, null))
                        if (cursor?.MoveToFirst() == true) name = cursor.GetString(0) ?? name;
                    // A provider supplies the display name, so it is reduced to one safe leaf before
                    // it becomes a path under the share cache.
                    name = SharedFileNames.SafeLeafName(name);
                    var folder = Path.Combine(sharesDirectory, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, name);
                    await using (var input = ContentResolver!.OpenInputStream(uri) ?? throw new IOException("无法读取分享的文件。"))
                    await using (var output = File.Create(path)) await input.CopyToAsync(output);
                    attachmentUris.Add(new Uri(path).AbsoluteUri);
                }

                // One share is one action: the text and every attachment become one activation list
                // for one surface, so they land in the same composer instead of replacing each other.
                activations.AddRange(ShareActivation.BuildActivations(hasText ? text : null, attachmentUris));
            }
            else return;
            if (targets.Count == 0) throw new InvalidOperationException("尚未启用支持此内容的工具。");
            async Task OpenAsync(string toolId)
            {
                // Wait for the Shell that actually owns the runtime, not for a timer: a share that
                // arrives during startup must land on the same instance the UI shows.
                var shell = await _shellReady.Task;
                AndroidStartupLog.Info("intent", $"Activating {toolId} with {activations.Count} activation(s)");
                // Every item of one share is activated on the same surface instance; the tool
                // accumulates them into its conversation, so a multi-file share arrives as one
                // selection instead of one page per file and an existing draft is never cleared.
                // ActivateAsync is the Shell's existing public contract and is awaited in order.
                foreach (var activation in activations)
                {
                    await shell.ActivateAsync(new ToolActivationRequest(toolId, "", activation));
                }
            }
            if (!chooseTool) await Dispatcher.UIThread.InvokeAsync(() => OpenAsync(targets[0].Id));
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
        finally { IntentGate.Release(); }
    }
}
