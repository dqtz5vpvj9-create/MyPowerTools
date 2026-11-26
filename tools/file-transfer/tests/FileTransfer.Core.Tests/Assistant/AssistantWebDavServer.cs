using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace FileTransfer.Tests;

internal sealed record WebDavRequest(string Method, string Path, string RelativePath, bool Authorized, string? Depth);

/// <summary>
/// A real HTTP/1.1 WebDAV-ish server on loopback: MKCOL, PUT, GET and PROPFIND with Basic authentication
/// against a real disk directory. The assistant tests use it instead of stubbing the client, so the wire
/// format, the auth header, the redirect rules and the publish ordering are actually exercised.
/// </summary>
internal sealed class AssistantWebDavServer : IAsyncDisposable
{
    public const string UserName = "mpt-relay";
    public const string Password = "relay-password";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<WebDavRequest> _requests = [];
    private readonly Task _loop;

    public AssistantWebDavServer(string root, int? port = null)
    {
        Root = root;
        Directory.CreateDirectory(root);
        for (var attempt = 0; ; attempt++)
        {
            Port = port ?? ReservePort();
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            try { _listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 5 && port is null) { }
        }
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Directory that backs <c>/dav/</c>.</summary>
    public string Root { get; }
    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/dav";
    public bool Running => _listener.IsListening;

    /// <summary>Test hook: return true after writing a response yourself (redirects, injected failures, hangs).</summary>
    public Func<HttpListenerContext, WebDavRequest, bool>? Intercept { get; set; }

    public IReadOnlyList<WebDavRequest> Requests { get { lock (_requests) return _requests.ToArray(); } }

    public void Stop()
    {
        try { _listener.Stop(); } catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
    }

    public string DiskPath(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));

    public bool Exists(string relativePath) => File.Exists(DiskPath(relativePath));

    public byte[] Read(string relativePath) => File.ReadAllBytes(DiskPath(relativePath));

    public int Count(string method, string? contains = null) => Requests.Count(request =>
        string.Equals(request.Method, method, StringComparison.Ordinal) &&
        (contains is null || request.Path.Contains(contains, StringComparison.Ordinal)));

    public async ValueTask DisposeAsync()
    {
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        Stop();
        try { await _loop; } catch (Exception) { }
        try { _lifetime.Dispose(); } catch (ObjectDisposedException) { }
    }

    /// <summary>A loopback port that nothing is listening on, so an "offline relay" can be simulated for real.</summary>
    public static int ReservePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url!.AbsolutePath;
        var relative = path.StartsWith("/dav", StringComparison.Ordinal) ? path["/dav".Length..] : path;
        var record = new WebDavRequest(request.HttpMethod, path, relative, IsAuthorized(request.Headers["Authorization"]), request.Headers["Depth"]);
        lock (_requests) _requests.Add(record);
        try
        {
            if (Intercept is not null && Intercept(context, record)) return;
            if (!record.Authorized && path.StartsWith("/dav", StringComparison.Ordinal)) { Respond(context, 401); return; }
            switch (request.HttpMethod)
            {
                case "MKCOL": Mkcol(context, relative); return;
                case "PUT": await PutAsync(context, relative); return;
                case "GET": Get(context, relative); return;
                case "PROPFIND": Propfind(context, relative); return;
                default: Respond(context, 405); return;
            }
        }
        catch (Exception)
        {
            try { Respond(context, 500); } catch (Exception) { }
        }
    }

    private void Mkcol(HttpListenerContext context, string relative)
    {
        // A collection URL carries a trailing slash; Path.GetDirectoryName would treat the last segment
        // as a file name, so normalize before looking for the parent that must already exist.
        var full = DiskPath(relative).TrimEnd(Path.DirectorySeparatorChar);
        if (Directory.Exists(full)) { Respond(context, 405); return; }   // WebDAV: existing collection
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(parent)) { Respond(context, 409); return; }
        Directory.CreateDirectory(full);
        Respond(context, 201);
    }

    private async Task PutAsync(HttpListenerContext context, string relative)
    {
        var full = DiskPath(relative);
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !Directory.Exists(parent)) { Respond(context, 409); return; }
        var body = await ReadBodyAsync(context.Request);
        var existed = File.Exists(full);
        await File.WriteAllBytesAsync(full, body);
        Respond(context, existed ? 204 : 201);
    }

    private void Get(HttpListenerContext context, string relative)
    {
        var full = DiskPath(relative);
        if (!File.Exists(full)) { Respond(context, 404); return; }
        Respond(context, 200, File.ReadAllBytes(full), "application/octet-stream");
    }

    private void Propfind(HttpListenerContext context, string relative)
    {
        var full = DiskPath(relative.TrimEnd('/'));
        var isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full)) { Respond(context, 404); return; }
        var entries = new List<(string Href, string FileSystem)>
        {
            (Href(relative, isDirectory), full)
        };
        if (isDirectory && (context.Request.Headers["Depth"] ?? "0") == "1")
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(full).OrderBy(value => value, StringComparer.Ordinal))
                entries.Add((Href(relative.TrimEnd('/') + "/" + Path.GetFileName(entry), Directory.Exists(entry)), entry));
        }
        Respond(context, 207, MultiStatus(entries), "application/xml; charset=utf-8");
    }

    private static string Href(string relative, bool directory) => "/dav" + relative + (directory ? "/" : "");

    private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody) return [];
        using var memory = new MemoryStream();
        await request.InputStream.CopyToAsync(memory);
        return memory.ToArray();
    }

    private static bool IsAuthorized(string? header)
    {
        if (header is null || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim())) == $"{UserName}:{Password}";
        }
        catch (FormatException) { return false; }
    }

    private static void Respond(HttpListenerContext context, int status, byte[]? body = null, string? contentType = null) =>
        Write(context, status, body, contentType);

    /// <summary>Writes a response from a test interceptor.</summary>
    public static void Write(HttpListenerContext context, int status, byte[]? body = null, string? contentType = null, string? location = null)
    {
        var response = context.Response;
        response.StatusCode = status;
        if (location is not null) response.AddHeader("Location", location);
        if (contentType is not null) response.ContentType = contentType;
        if (body is null)
        {
            response.ContentLength64 = 0;
        }
        else
        {
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body, 0, body.Length);
        }
        response.Close();
    }

    private static byte[] MultiStatus(IEnumerable<(string Href, string FileSystem)> entries)
    {
        XNamespace dav = "DAV:";
        var responses = new List<XElement>();
        foreach (var entry in entries)
        {
            var isDirectory = Directory.Exists(entry.FileSystem);
            var resourceType = new XElement(dav + "resourcetype");
            if (isDirectory) resourceType.Add(new XElement(dav + "collection"));
            responses.Add(new XElement(dav + "response",
                new XElement(dav + "href", entry.Href),
                new XElement(dav + "propstat",
                    new XElement(dav + "prop",
                        new XElement(dav + "getlastmodified", File.GetLastWriteTimeUtc(entry.FileSystem).ToString("R")),
                        new XElement(dav + "getcontentlength", isDirectory ? 0 : new FileInfo(entry.FileSystem).Length),
                        resourceType),
                    new XElement(dav + "status", "HTTP/1.1 200 OK"))));
        }
        var document = new XDocument(new XElement(dav + "multistatus", responses));
        return Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + document.ToString(SaveOptions.DisableFormatting));
    }
}
