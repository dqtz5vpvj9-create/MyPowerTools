using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.WebToolHost;

/// <summary>Separate provider login window. Only its parent pipe receives the credential.</summary>
internal sealed class CloudLoginWindow : Form
{
    private readonly string _provider;
    private readonly WebView2 _browser = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Top, AutoSize = false, Height = 52, Padding = new Padding(12) };
    private bool _completed;

    private CloudLoginWindow(string provider)
    {
        _provider = provider;
        Text = provider == "quark" ? "连接夸克网盘 · MyPowerTools" : "连接百度网盘 · MyPowerTools";
        Width = 1040; Height = 800;
        Controls.Add(_browser); Controls.Add(_status);
        _status.Text = "请在网盘页面登录。MPT 不会读取你的密码。";
        if (provider == "quark")
        {
            var connect = new Button { Text = "已登录，连接此账号", Dock = DockStyle.Bottom, Height = 52 };
            connect.Click += async (_, _) => await ConnectQuarkAsync();
            Controls.Add(connect);
        }
        Shown += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) => { if (!_completed) Console.Out.WriteLine("null"); };
    }

    internal static int Run(string provider)
    {
        if (provider is not ("quark" or "baidu")) return 2;
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var window = new CloudLoginWindow(provider);
            Application.Run(window);
            return 0;
        }
        catch { return 1; }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyPowerTools", "CloudLogin", Guid.NewGuid().ToString("N"));
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            environment.BrowserProcessExited += (_, _) =>
            {
                try { if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true); }
                catch (IOException) { /* A remaining native handle can delay cleanup; no account references this profile. */ }
                catch (UnauthorizedAccessException) { /* Do not relax filesystem permissions to clean a profile. */ }
            };
            await _browser.EnsureCoreWebView2Async(environment);
            if (IsDisposed) return;
            _browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _browser.CoreWebView2.Settings.IsWebMessageEnabled = false;
            _browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            _browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            _browser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https") { args.Cancel = true; return; }
                if (_provider == "baidu" && uri.Host == "api.oplist.org" && uri.AbsolutePath == "/" && uri.Fragment.Length > 1)
                {
                    args.Cancel = true;
                    var token = CloudAuthorizationCallback.BaiduRefreshToken(args.Uri);
                    if (token is not null) Complete("refreshToken", token);
                    else _status.Text = "百度授权未完成，请关闭窗口后重试。";
                }
            };
            if (_provider == "quark") { _browser.CoreWebView2.Navigate("https://pan.quark.cn/"); return; }
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await http.GetAsync("https://api.oplist.org/baiduyun/requests?server_use=true&driver_txt=baiduyun_go");
            response.EnsureSuccessStatusCode();
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var url = data.RootElement.GetProperty("text").GetString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var login) || login.Scheme != "https" ||
                login.Host is not ("openapi.baidu.com" or "passport.baidu.com")) throw new InvalidDataException();
            if (IsDisposed) return;
            foreach (var pair in new[] { ("server_use", "true"), ("driver_txt", "baiduyun_go") })
            {
                var cookie = _browser.CoreWebView2.CookieManager.CreateCookie(pair.Item1, pair.Item2, "api.oplist.org", "/");
                cookie.IsSecure = true;
                _browser.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
            }
            var uriBuilder = new UriBuilder(login);
            uriBuilder.Query = login.Query.TrimStart('?') + "&qrcode=1";
            _status.Text = "请确认百度授权页面的应用及权限。授权由 OpenList 提供。";
            _browser.CoreWebView2.Navigate(uriBuilder.Uri.AbsoluteUri);
        }
        catch { if (!IsDisposed) _status.Text = "无法打开网盘登录，请关闭窗口后重试。"; }
    }

    private async Task ConnectQuarkAsync()
    {
        if (_browser.CoreWebView2 is null) return;
        try
        {
            var cookies = await _browser.CoreWebView2.CookieManager.GetCookiesAsync("https://pan.quark.cn");
            if (IsDisposed) return;
            if (!cookies.Any(c => c.Name == "__puus" && !string.IsNullOrEmpty(c.Value)))
            { _status.Text = "请先在夸克页面完成登录，再连接此账号。"; return; }
            Complete("cookie", string.Join("; ", cookies.Select(c => c.Name + "=" + c.Value)));
        }
        catch { if (!IsDisposed) _status.Text = "无法取得登录结果，请关闭窗口后重试。"; }
    }

    private void Complete(string kind, string credential)
    {
        if (_completed) return;
        _completed = true;
        Console.Out.WriteLine(JsonSerializer.Serialize(new MptCloudAuthorizationResult(_provider, kind, credential)));
        Console.Out.Flush();
        Close();
    }
}
