using Microsoft.Web.WebView2.Core;

namespace MyPowerTools.WebToolHost;

internal sealed class SmartBirdHostWindow : Form
{
    public const string FixedDashboardUrl = "http://127.0.0.1:19002/";

    private readonly nint _parent;
    private readonly Uri _dashboardUri;
    private readonly Uri _origin;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _webView;
    private bool _initializing;
    private bool _controllerReady;
    private bool _shellAllowsVisibility;
    private bool _navigationReady;
    private bool _surfaceVisible;

    private SmartBirdHostWindow(nint parent, Uri dashboardUri)
    {
        _parent = parent;
        _dashboardUri = dashboardUri;
        _origin = new UriBuilder(
            dashboardUri.Scheme,
            dashboardUri.Host,
            dashboardUri.Port,
            "/").Uri;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.White;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Parent = _parent;
            parameters.Style = unchecked((int)(
                Win32Native.WsChild |
                Win32Native.WsClipChildren |
                Win32Native.WsClipSiblings));
            parameters.ExStyle = 0;
            parameters.Caption = "";
            return parameters;
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(value && _shellAllowsVisibility);
    }

    public static SmartBirdHostWindow Create(
        nint parent,
        uint expectedParentProcessId,
        Uri dashboardUri)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WebToolHost requires Windows.");
        }
        if (parent == 0 || !Win32Native.IsWindow(parent))
        {
            throw new InvalidOperationException("The Shell parent window is unavailable.");
        }
        if (!IsSupportedDashboardUri(dashboardUri))
        {
            throw new InvalidOperationException("The SmartBird dashboard URL is outside the local HTTP policy.");
        }

        _ = Win32Native.GetWindowThreadProcessId(parent, out var actualParentProcessId);
        if (actualParentProcessId != expectedParentProcessId ||
            actualParentProcessId == (uint)Environment.ProcessId)
        {
            throw new InvalidOperationException("The Shell parent window identity did not match the launch contract.");
        }

        var result = new SmartBirdHostWindow(parent, dashboardUri);
        _ = result.Handle;
        if (Win32Native.GetParent(result.Handle) != parent)
        {
            _ = Win32Native.SetParent(result.Handle, parent);
        }
        if (Win32Native.GetParent(result.Handle) != parent)
        {
            result.Dispose();
            throw new InvalidOperationException("WebToolHost could not attach its child window to the Shell window.");
        }
        result.SetBounds(0, 0, 1, 1, BoundsSpecified.All);
        result.Hide();
        return result;
    }

    public async Task InitializeAsync()
    {
        if (IsDisposed || _initializing || _controllerReady)
        {
            return;
        }

        _initializing = true;
        WebToolHostProtocol.WriteState("loading", phase: "initializing");
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyPowerTools",
                "WebView2",
                "SmartBird",
                "WebToolHost");
            Directory.CreateDirectory(userDataFolder);
            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);
            _controller = await _environment.CreateCoreWebView2ControllerAsync(Handle);
            if (IsDisposed)
            {
                return;
            }

            var webView = _controller.CoreWebView2;
            _webView = webView;
            _controller.DefaultBackgroundColor = Color.White;
            _controller.IsVisible = false;
            _controller.AllowExternalDrop = false;
            webView.Settings.AreDevToolsEnabled = false;
            webView.Settings.AreDefaultContextMenusEnabled = true;
            webView.Settings.AreBrowserAcceleratorKeysEnabled = false;
            webView.Settings.AreDefaultScriptDialogsEnabled = false;
            webView.Settings.AreHostObjectsAllowed = false;
            webView.Settings.IsBuiltInErrorPageEnabled = false;
            webView.Settings.IsWebMessageEnabled = true;
            webView.Settings.IsGeneralAutofillEnabled = false;
            webView.Settings.IsPasswordAutosaveEnabled = false;
            webView.Settings.IsStatusBarEnabled = false;
            webView.Settings.IsZoomControlEnabled = true;
            webView.NavigationStarting += OnNavigationStarting;
            webView.NavigationCompleted += OnNavigationCompleted;
            webView.NewWindowRequested += OnNewWindowRequested;
            webView.PermissionRequested += OnPermissionRequested;
            webView.DownloadStarting += OnDownloadStarting;
            webView.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            webView.WebResourceRequested += OnWebResourceRequested;
            webView.ProcessFailed += OnProcessFailed;
            webView.WebMessageReceived += OnWebMessageReceived;
            _controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
            _controller.MoveFocusRequested += OnMoveFocusRequested;
            UpdateControllerBounds();
            _controllerReady = true;
            WebToolHostProtocol.WriteState("loading", phase: "controller-ready");
            webView.Navigate(_dashboardUri.AbsoluteUri);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            CloseController();
            WebToolHostProtocol.WriteState(
                "unavailable",
                "此电脑尚未安装 Microsoft Edge WebView2 Runtime。可在系统浏览器中打开控制台。",
                "runtime-missing");
            Close();
        }
        catch (Exception ex)
        {
            CloseController();
            WebToolHostProtocol.WriteState(
                "failed",
                FriendlyWebViewError(ex),
                "initialization-failed");
            Close();
        }
        finally
        {
            _initializing = false;
        }
    }

    public void ApplyBounds(HostCommand command)
    {
        if (IsDisposed)
        {
            return;
        }
        var width = Math.Clamp(command.Width, 1, 32767);
        var height = Math.Clamp(command.Height, 1, 32767);
        SetBounds(command.X, command.Y, width, height, BoundsSpecified.All);
        ApplyClipRegion(command, width, height);
        UpdateControllerBounds();
        if (command.Visible)
        {
            _shellAllowsVisibility = true;
            base.SetVisibleCore(true);
            PaintOpaquePlaceholder();
            RevealSurfaceIfReady();
        }
        else
        {
            _shellAllowsVisibility = false;
            HideSurface();
            base.SetVisibleCore(false);
        }
    }

    public void Reload()
    {
        if (_controllerReady && _webView is not null)
        {
            _navigationReady = false;
            HideSurface();
            WebToolHostProtocol.WriteState("loading", phase: "reload");
            _webView.Reload();
            return;
        }
        _ = InitializeAsync();
    }

    public void PostBridgeResponse(System.Text.Json.JsonElement payload)
    {
        if (_webView is not null && payload.ValueKind is not System.Text.Json.JsonValueKind.Undefined)
        {
            _webView.PostWebMessageAsJson(payload.GetRawText());
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArguments)
    {
        if (!Uri.TryCreate(eventArguments.Source, UriKind.Absolute, out var source) || !HasSameOrigin(source))
        {
            return;
        }
        var json = eventArguments.WebMessageAsJson;
        if (json.Length <= 16 * 1024)
        {
            WebToolHostProtocol.WriteBridgeRequest(json);
        }
    }

    public void FocusWebView(string direction)
    {
        if (_controller is null)
        {
            return;
        }
        var reason = direction switch
        {
            "next" => CoreWebView2MoveFocusReason.Next,
            "previous" => CoreWebView2MoveFocusReason.Previous,
            _ => CoreWebView2MoveFocusReason.Programmatic
        };
        _controller.MoveFocus(reason);
    }

    public void RequestClose()
    {
        if (!IsDisposed)
        {
            Close();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs eventArguments)
    {
        CloseController();
        base.OnFormClosed(eventArguments);
        Application.ExitThread();
    }

    public static bool IsSupportedDashboardUri(Uri? target)
    {
        return target is { IsAbsoluteUri: true } &&
               (target.IsFile || target.Scheme is "http" or "https") &&
               string.IsNullOrEmpty(target.UserInfo);
    }

    private bool HasSameOrigin(Uri target)
    {
        if (_dashboardUri.IsFile && target.IsFile)
        {
            var root = Path.GetDirectoryName(_dashboardUri.LocalPath)!
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return Path.GetFullPath(target.LocalPath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        return IsSupportedDashboardUri(target) &&
               string.Equals(target.Scheme, _origin.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(target.Host, _origin.Host, StringComparison.Ordinal) &&
               target.Port == _origin.Port;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) || !HasSameOrigin(target))
        {
            args.Cancel = true;
            return;
        }
        _navigationReady = false;
        HideSurface();
        WebToolHostProtocol.WriteState("loading", phase: "navigation");
    }

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
    }

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess)
        {
            _navigationReady = true;
            WebToolHostProtocol.WriteState("loading", phase: "document-ready");
            BeginInvoke(RevealSurfaceIfReady);
            return;
        }
        HideSurface();
        WebToolHostProtocol.WriteState(
            "failed",
            $"控制台导航失败：{args.WebErrorStatus}。可刷新或在浏览器中打开。",
            "navigation-failed");
    }

    private void RevealSurfaceIfReady()
    {
        if (IsDisposed ||
            !_navigationReady ||
            !_controllerReady ||
            !_shellAllowsVisibility ||
            _controller is null ||
            _surfaceVisible)
        {
            return;
        }

        PaintOpaquePlaceholder();
        _controller.IsVisible = true;
        _surfaceVisible = true;
        WebToolHostProtocol.WriteState("ready", phase: "surface-visible");
    }

    private void HideSurface()
    {
        if (_controller is not null)
        {
            _controller.IsVisible = false;
        }
        _surfaceVisible = false;
        PaintOpaquePlaceholder();
    }

    private void PaintOpaquePlaceholder()
    {
        if (!_shellAllowsVisibility || _surfaceVisible || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        Invalidate(invalidateChildren: true);
        Update();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (_webView is not null &&
            Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) &&
            HasSameOrigin(target))
        {
            _webView.Navigate(target.AbsoluteUri);
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var resourceUri) && HasSameOrigin(resourceUri))
        {
            return;
        }
        if (_environment is not null)
        {
            args.Response = _environment.CreateWebResourceResponse(
                Stream.Null,
                403,
                "Blocked by SmartBird same-origin policy",
                "Content-Type: text/plain; charset=utf-8");
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        WebToolHostProtocol.WriteState(
            "failed",
            $"WebView2 进程异常退出：{args.ProcessFailedKind}。可刷新页面后重试。",
            "webview-process-failed");
        BeginInvoke(Close);
    }

    private static void OnMoveFocusRequested(
        object? sender,
        CoreWebView2MoveFocusRequestedEventArgs args)
    {
        var direction = args.Reason switch
        {
            CoreWebView2MoveFocusReason.Next => "next",
            CoreWebView2MoveFocusReason.Previous => "previous",
            _ => ""
        };
        if (direction.Length == 0)
        {
            return;
        }
        args.Handled = true;
        WebToolHostProtocol.WriteFocusMove(direction);
    }

    private static void OnAcceleratorKeyPressed(
        object? sender,
        CoreWebView2AcceleratorKeyPressedEventArgs args)
    {
        if (args.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown) ||
            (args.KeyEventLParam & 0x40000000) != 0)
        {
            return;
        }

        var control = IsKeyDown(Win32Native.VkControl);
        var alt = IsKeyDown(Win32Native.VkMenu);
        var shift = IsKeyDown(Win32Native.VkShift);
        var virtualKey = args.VirtualKey;
        string? gesture = null;
        if (control && !alt && !shift && virtualKey is 0x46 or 0x4B or 0x52)
        {
            gesture = virtualKey switch
            {
                0x46 => "Ctrl+F",
                0x4B => "Ctrl+K",
                _ => "Ctrl+R"
            };
        }
        else if (control && alt && !shift && virtualKey == 0x20)
        {
            gesture = "Ctrl+Alt+Space";
        }
        else if (!control && !alt && !shift && virtualKey == 0x74)
        {
            gesture = "F5";
        }
        else if (!control && !alt && !shift && virtualKey == 0x1B)
        {
            gesture = "Escape";
        }
        else if (control && !alt && !shift && virtualKey is >= 0x31 and <= 0x36)
        {
            gesture = $"Ctrl+{(char)virtualKey}";
        }

        if (gesture is null)
        {
            return;
        }
        args.Handled = true;
        WebToolHostProtocol.WriteShortcut(gesture);
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (Win32Native.GetKeyState(virtualKey) & 0x8000) != 0;
    }

    private void ApplyClipRegion(HostCommand command, int width, int height)
    {
        var left = Math.Clamp(command.ClipX, 0, width);
        var top = Math.Clamp(command.ClipY, 0, height);
        var right = Math.Clamp(left + Math.Max(0, command.ClipWidth), left, width);
        var bottom = Math.Clamp(top + Math.Max(0, command.ClipHeight), top, height);
        var region = Win32Native.CreateRectRgn(left, top, right, bottom);
        if (region == 0)
        {
            return;
        }
        if (Win32Native.SetWindowRgn(Handle, region, redraw: true) == 0)
        {
            _ = Win32Native.DeleteObject(region);
        }
    }

    private void UpdateControllerBounds()
    {
        if (_controller is null || !Win32Native.GetClientRect(Handle, out var rectangle))
        {
            return;
        }
        _controller.Bounds = new Rectangle(
            0,
            0,
            Math.Max(1, rectangle.Right - rectangle.Left),
            Math.Max(1, rectangle.Bottom - rectangle.Top));
    }

    private void CloseController()
    {
        if (_webView is { } webView)
        {
            webView.NavigationStarting -= OnNavigationStarting;
            webView.NavigationCompleted -= OnNavigationCompleted;
            webView.NewWindowRequested -= OnNewWindowRequested;
            webView.PermissionRequested -= OnPermissionRequested;
            webView.DownloadStarting -= OnDownloadStarting;
            webView.WebResourceRequested -= OnWebResourceRequested;
            webView.ProcessFailed -= OnProcessFailed;
        }
        if (_controller is not null)
        {
            _controller.AcceleratorKeyPressed -= OnAcceleratorKeyPressed;
            _controller.MoveFocusRequested -= OnMoveFocusRequested;
        }
        _controllerReady = false;
        _navigationReady = false;
        _surfaceVisible = false;
        _webView = null;
        try
        {
            _controller?.Close();
        }
        catch
        {
        }
        _controller = null;
        _environment = null;
    }

    private static string FriendlyWebViewError(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => "WebView2 无法访问独立宿主的数据目录。可在系统浏览器中打开控制台。",
            _ => "独立 WebView2 宿主初始化失败。Shell 仍可继续使用，可重试或在系统浏览器中打开控制台。"
        };
    }
}
