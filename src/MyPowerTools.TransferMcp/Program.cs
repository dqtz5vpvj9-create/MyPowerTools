using System.Net;
using System.Security.Cryptography;
using System.Text;
using MyPowerTools.TransferMcp;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
// HTTP access has its own capability, never the Runner's broader IPC credential.
var tokenFile = Environment.GetEnvironmentVariable("MPT_MCP_TOKEN_FILE")
    ?? throw new InvalidOperationException("MPT_MCP_TOKEN_FILE must identify a private token file.");
var secret = (await File.ReadAllTextAsync(tokenFile)).Trim();
if (secret.Length < 32) throw new InvalidOperationException("MCP token must contain at least 32 characters.");
var port = int.Parse(Environment.GetEnvironmentVariable("MPT_MCP_PORT") ?? "17843");
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton<TransferAccess>();
builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<TransferTools>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    // No browser origins or DNS aliases: even loopback must not expose host file tools
    // to an arbitrary website or unauthenticated local user.
    if (context.Request.Headers.ContainsKey("Origin") || context.Request.Host.Host != "127.0.0.1")
    { context.Response.StatusCode = 403; return; }
    var authorization = context.Request.Headers.Authorization.ToString();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorization), Encoding.UTF8.GetBytes("Bearer " + secret)))
    { context.Response.StatusCode = 401; return; }
    await next(context);
});
app.MapGet("/health", (TransferAccess access) => new { status = "ready", activeCalls = access.ActiveCalls, activeSubscriptions = access.ActiveSubscriptions, sessions = 0, processId = Environment.ProcessId });
app.MapMcp("/mcp");
await app.RunAsync();
