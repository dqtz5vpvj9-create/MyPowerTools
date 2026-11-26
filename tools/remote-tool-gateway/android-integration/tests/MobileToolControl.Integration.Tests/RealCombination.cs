using System.Net;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using MobileToolControl.Android;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using RemoteToolGateway.Core;

[assembly: AvaloniaTestApplication(typeof(MyPowerTools.MobileToolControl.Integration.Tests.TestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace MyPowerTools.MobileToolControl.Integration.Tests;

/// <summary>
/// Headless host for the real phone page: Fluent plus the real SDK mobile theme, rendered through Skia,
/// exactly like the logic suite, so the integration runs under the shipped classes and tokens.
/// </summary>
public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://MyPowerTools.AvaloniaSdk/"))
        {
            Source = new Uri("avares://MyPowerTools.AvaloniaSdk/Themes/MptMobileTheme.axaml")
        });
        RequestedThemeVariant = ThemeVariant.Light;
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Test-only endpoint policy for the phone module: the real gateway listens on 127.0.0.1 in this
/// suite. Production always constructs <c>TailnetEndpointPolicy</c>; nothing outside a test can set
/// this, and the module still rejects every non-loopback address.
/// </summary>
internal sealed class LoopbackEndpointPolicy : IMobileToolEndpointPolicy
{
    public bool Allows(IPAddress address) => IPAddress.IsLoopback(address);

    public string Rule => "loopback（仅集成测试）";
}

/// <summary>
/// Dispatcher helpers for the integration tests. The test stays on the headless UI thread and pumps,
/// because an async test body would resume on a thread-pool thread and every control access there
/// would fail its thread check.
/// </summary>
internal static class IntegrationHarness
{
    public static void Pump()
    {
        for (var pass = 0; pass < 4; pass++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    public static void Complete(Task task)
    {
        for (var pass = 0; pass < 2000 && !task.IsCompleted; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Pump();
            if (!task.IsCompleted)
            {
                Thread.Sleep(2);
            }
        }

        Assert.True(task.IsCompleted, "操作没有在预期时间内完成。");
        task.GetAwaiter().GetResult();
        Pump();
    }

    public static T Complete<T>(Task<T> task)
    {
        Complete((Task)task);
        return task.Result;
    }

    public static void WaitFor(Func<bool> condition, string because)
    {
        for (var pass = 0; pass < 2000 && !condition(); pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Pump();
            if (!condition())
            {
                Thread.Sleep(2);
            }
        }

        Assert.True(condition(), because);
    }
}

/// <summary>Everything the desktop gateway needs before the phone is built.</summary>
internal sealed record RealGatewaySetup(
    RemoteToolGatewayService Gateway,
    InMemorySecretStore Secrets,
    string Root,
    string Code,
    string Endpoint,
    string GrantId);

/// <summary>
/// The real combination: desktop gateway service + its real HTTP listener, the real phone module and
/// HTTP client, and the real page view model. Only <see cref="IHostControlBridge"/> is a fake.
/// </summary>
internal sealed class RealCombination : IDisposable
{
    private RealCombination(
        RemoteToolGatewayService gateway,
        MobileToolControlModule module,
        MobileToolControlView view,
        FakeHostControlBridge bridge,
        InMemorySecretStore secrets,
        string root,
        string code,
        string endpoint,
        string grantId)
    {
        Gateway = gateway;
        Module = module;
        View = view;
        Bridge = bridge;
        Secrets = secrets;
        Root = root;
        Code = code;
        Endpoint = endpoint;
        GrantId = grantId;
    }

    public RemoteToolGatewayService Gateway { get; }

    public MobileToolControlModule Module { get; }

    public MobileToolControlView View { get; }

    public FakeHostControlBridge Bridge { get; }

    public InMemorySecretStore Secrets { get; }

    public string Root { get; }

    public string Code { get; }

    public string Endpoint { get; }

    public string GrantId { get; }

    public MobileToolControlViewModel Vm => View.ViewModel;

    public string DevicesFile => Path.Combine(Root, "phone", "devices.json");

    /// <summary>The phone's own copy of the grant token, read through the module's secret naming.</summary>
    public string PhoneToken => Secrets.Values
        .Single(pair => pair.Key.Contains("mobile-tool-control", StringComparison.Ordinal))
        .Value;

    /// <summary>Starts the real gateway on a loopback listener and creates a real grant + code.</summary>
    public static RealGatewaySetup StartGateway(FakeHostControlBridge bridge, IReadOnlyList<string> grantedCommandIds)
    {
        var root = Path.Combine(ResolveTempRoot(), "mpt-control-integration", Guid.NewGuid().ToString("N"));
        var desktop = Path.Combine(root, "desktop");
        Directory.CreateDirectory(desktop);
        Directory.CreateDirectory(Path.Combine(root, "phone"));

        var secrets = new InMemorySecretStore();
        var gateway = new RemoteToolGatewayService(
            desktop,
            secrets,
            bridge,
            new RemoteToolGatewayOptions
            {
                DeviceName = "工作电脑",
                Platform = "windows",
                AllowLoopbackTransport = true
            });
        IntegrationHarness.Complete(gateway.InitializeAsync(CancellationToken.None));
        var (grant, token) = IntegrationHarness.Complete(
            gateway.Grants.CreateAsync("测试手机", grantedCommandIds, false, CancellationToken.None));
        IntegrationHarness.Complete(gateway.StartListenerAsync("127.0.0.1", 0, CancellationToken.None));
        // The listener reports its real ephemeral port; the returned string is built from the
        // requested port and would read :0 for an ephemeral bind.
        var endpoint = $"http://127.0.0.1:{gateway.ListenerPort}";
        var code = ControlConnectionCode.Encode(new ControlConnection(
            ControlConnectionCode.CurrentVersion,
            endpoint,
            grant.GrantId,
            "工作电脑",
            token));
        return new RealGatewaySetup(gateway, secrets, root, code, endpoint, grant.GrantId);
    }

    /// <summary>Builds the real module and the real page on the calling (UI) thread.</summary>
    public static RealCombination Create(RealGatewaySetup setup, FakeHostControlBridge bridge)
    {
        var phone = Path.Combine(setup.Root, "phone");
        var module = new MobileToolControlModule { EndpointPolicy = new LoopbackEndpointPolicy() };
        var context = new ModuleContext(
            "integration-host",
            "1.0",
            MobileToolControlOptions.PackageId,
            MobileToolControlOptions.ModuleId,
            phone,
            Path.Combine(setup.Root, "cache"),
            Path.Combine(setup.Root, "logs"),
            "android-arm64",
            ["secret.store"],
            new Dictionary<string, object>(StringComparer.Ordinal) { ["secret.store"] = setup.Secrets });
        IntegrationHarness.Complete(module.InitializeAsync(context, CancellationToken.None).AsTask());

        var surface = new MptAvaloniaSurfaceContext(
            MobileToolControlContract.ToolId,
            "workspace",
            phone,
            "light",
            (commandId, args, cancellationToken) => module
                .ExecuteCommandAsync(
                    new CommandRequest(Guid.NewGuid().ToString("N"), commandId, args ?? new JsonObject()),
                    cancellationToken)
                .AsTask(),
            (_, _, _) => Task.CompletedTask,
            null!,
            _ => { });
        var view = new MobileToolControlView(surface);
        // The Shell activates the page when it is shown; a visible page is what allows the short-lived
        // status reads of a running invocation.
        view.ViewModel.Activate();
        IntegrationHarness.Pump();

        return new RealCombination(
            setup.Gateway,
            module,
            view,
            bridge,
            setup.Secrets,
            setup.Root,
            setup.Code,
            setup.Endpoint,
            setup.GrantId);
    }

    /// <summary>Imports the real desktop connection code through the page's own import flow.</summary>
    public void Import()
    {
        var previewed = IntegrationHarness.Complete(Vm.OpenImportFromCodeAsync(Code));
        Assert.True(previewed, "连接码预览失败：" + Vm.ImportError);
        IntegrationHarness.Complete(((MptAsyncRelayCommand)Vm.ConfirmImportCommand).ExecuteAsync());
        IntegrationHarness.Pump();
        Assert.False(Vm.IsImportSheetOpen);
    }

    public MobileToolDeviceItem Device()
    {
        var device = Vm.Devices.FirstOrDefault(item =>
            string.Equals(item.DeviceId, GrantId, StringComparison.Ordinal));
        Assert.NotNull(device);
        return device!;
    }

    public void Dispose()
    {
        IntegrationHarness.Complete(DisposeCoreAsync());
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Module.DisposeAsync(CancellationToken.None);
        await Gateway.DisposeAsync();
    }

    private static string ResolveTempRoot()
    {
        const string cacheRoot = "/mnt/cache/data-cache";
        if (Directory.Exists(cacheRoot))
        {
            try
            {
                var probe = Path.Combine(cacheRoot, ".mpt-integration-probe");
                Directory.CreateDirectory(probe);
                Directory.Delete(probe);
                return cacheRoot;
            }
            catch (Exception)
            {
                // Fall through to the repository verify directory.
            }
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
        {
            directory = directory.Parent;
        }

        var verify = Path.Combine(
            directory?.FullName ?? AppContext.BaseDirectory,
            "artifacts", ".tmp-android-verify", "mobile-tool-control-integration");
        Directory.CreateDirectory(verify);
        return verify;
    }
}
