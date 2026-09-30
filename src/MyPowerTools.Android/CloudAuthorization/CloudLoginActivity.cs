using System.Text.Json;
using MyPowerTools.AvaloniaSdk;
using A = global::Android;

namespace MyPowerTools.Android.CloudAuthorization;

[A.App.Activity(Exported = false, Theme = "@style/Theme.MyPowerTools", ExcludeFromRecents = true,
    WindowSoftInputMode = A.Views.SoftInput.AdjustResize,
    ConfigurationChanges = A.Content.PM.ConfigChanges.Orientation | A.Content.PM.ConfigChanges.ScreenSize)]
public sealed class CloudLoginActivity : A.App.Activity
{
    private CloudLogin.LoginRequest? _request;
    private A.Webkit.WebView? _browser;
    private A.Widget.TextView? _status;

    protected override void OnCreate(A.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _request = CloudLogin.Attach(this);
        if (_request is null) { Finish(); return; }
        Window?.SetFlags(A.Views.WindowManagerFlags.Secure, A.Views.WindowManagerFlags.Secure);
        var layout = new A.Widget.LinearLayout(this) { Orientation = A.Widget.Orientation.Vertical };
        var header = new A.Widget.LinearLayout(this) { Orientation = A.Widget.Orientation.Horizontal };
        var back = new A.Widget.Button(this) { Text = "取消" };
        back.Click += (_, _) => Cancel();
        header.AddView(back);
        var title = new A.Widget.TextView(this) { Text = _request.ProviderId == "quark" ? "连接夸克网盘" : "连接百度网盘", TextSize = 20 };
        header.AddView(title, new A.Widget.LinearLayout.LayoutParams(0, -2, 1));
        layout.AddView(header);
        _status = new A.Widget.TextView(this) { Text = "请在网盘页面登录。MPT 不会读取你的密码。", TextSize = 14 };
        _status.SetPadding(20, 12, 20, 12);
        layout.AddView(_status);
        _browser = new A.Webkit.WebView(this);
        _browser.Settings.JavaScriptEnabled = true;
        _browser.Settings.DomStorageEnabled = true;
        _browser.Settings.AllowFileAccess = false;
        _browser.Settings.AllowContentAccess = false;
        _browser.Settings.MixedContentMode = A.Webkit.MixedContentHandling.NeverAllow;
        _browser.SetWebViewClient(new LoginBrowser(this));
        layout.AddView(_browser, new A.Widget.LinearLayout.LayoutParams(-1, 0, 1));
        if (_request.ProviderId == "quark")
        {
            var connect = new A.Widget.Button(this) { Text = "已登录，连接此账号" };
            connect.Click += (_, _) => ConnectQuark();
            layout.AddView(connect);
            ClearLoginSession(() => _browser?.LoadUrl("https://pan.quark.cn/"));
        }
        else ClearLoginSession(() => _ = StartBaiduAsync());
        SetContentView(layout);
    }

    private async Task StartBaiduAsync()
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await http.GetAsync("https://api.oplist.org/baiduyun/requests?server_use=true&driver_txt=baiduyun_go");
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var url = body.RootElement.GetProperty("text").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !(uri.Host == "openapi.baidu.com" || uri.Host == "passport.baidu.com"))
                throw new InvalidDataException();
            RunOnUiThread(() =>
            {
                if (IsFinishing || _browser is null) return;
                var cookies = A.Webkit.CookieManager.Instance!;
                cookies.SetCookie("https://api.oplist.org", "server_use=true; Secure; Path=/");
                cookies.SetCookie("https://api.oplist.org", "driver_txt=baiduyun_go; Secure; Path=/");
                _status!.Text = "请确认百度授权页面显示的应用及权限。授权由 OpenList 提供。";
                _browser.LoadUrl(uri.AbsoluteUri);
            });
        }
        catch
        {
            RunOnUiThread(() => { if (!IsFinishing && _status is not null) _status.Text = "无法打开百度授权，请取消后重试。"; });
        }
    }

    private void ConnectQuark()
    {
        var cookie = A.Webkit.CookieManager.Instance?.GetCookie("https://pan.quark.cn");
        if (string.IsNullOrWhiteSpace(cookie) || !cookie.Split(';').Any(part => part.Trim().StartsWith("__puus=", StringComparison.Ordinal)))
        {
            _status!.Text = "尚未取得登录信息，请在夸克页面完成登录后再连接。";
            return;
        }
        CloudLogin.Complete(_request!, new MptCloudAuthorizationResult("quark", "cookie", cookie));
    }

    private bool HandleNavigation(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return true;
        if (_request?.ProviderId != "baidu" || uri.Host != "api.oplist.org" || uri.AbsolutePath != "/" || uri.Fragment.Length < 2) return false;
        var refreshToken = CloudAuthorizationCallback.BaiduRefreshToken(url);
        if (refreshToken is not null)
        {
            _browser?.StopLoading();
            CloudLogin.Complete(_request, new MptCloudAuthorizationResult("baidu", "refreshToken", refreshToken));
        }
        else _status!.Text = "百度授权未完成，请取消后重试。";
        return true;
    }

    private void Cancel() { if (_request is { } request) CloudLogin.Complete(request, null); else Finish(); }

    // The Android host currently owns no other WebView: its cloud login browser is on-demand and
    // only one login request can exist. Clear this app's browser session, not the external browser.
    private void ClearLoginSession(Action next)
    {
        A.Webkit.WebStorage.Instance?.DeleteAllData();
        var cookies = A.Webkit.CookieManager.Instance;
        if (cookies is null) { next(); return; }
        cookies.RemoveAllCookies(new LoginSessionCleared(() => { if (!IsFinishing) next(); }));
    }

    public override void OnBackPressed()
    {
        if (_browser?.CanGoBack() == true) _browser.GoBack(); else Cancel();
    }

    protected override void OnDestroy()
    {
        _browser?.StopLoading();
        if (CloudLogin.CanClearSession(_request))
        {
            A.Webkit.CookieManager.Instance?.RemoveAllCookies(null);
            A.Webkit.WebStorage.Instance?.DeleteAllData();
        }
        _browser?.Destroy();
        _browser = null;
        if (_request is { } request && !request.Result.Task.IsCompleted) CloudLogin.Complete(request, null);
        base.OnDestroy();
    }

    private sealed class LoginBrowser(CloudLoginActivity owner) : A.Webkit.WebViewClient
    {
        public override bool ShouldOverrideUrlLoading(A.Webkit.WebView? view, A.Webkit.IWebResourceRequest? request) =>
            owner.HandleNavigation(request?.Url?.ToString());

        public override void OnPageStarted(A.Webkit.WebView? view, string? url, A.Graphics.Bitmap? favicon)
        {
            if (owner.HandleNavigation(url)) view?.StopLoading();
        }
    }

    private sealed class LoginSessionCleared(Action done) : Java.Lang.Object, A.Webkit.IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value) => done();
    }
}
