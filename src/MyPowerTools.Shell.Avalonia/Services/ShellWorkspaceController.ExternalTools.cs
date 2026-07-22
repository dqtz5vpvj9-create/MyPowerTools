using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.ServiceManager.Client;
using MyPowerTools.HostControl;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private static readonly HttpClient ExternalToolHttpClient = new();
    private static readonly Regex ExternalToolSettingToken = new(
        @"\$\{settings\.(?<name>[A-Za-z0-9_.-]+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private async Task LoadExternalSdkToolAsync(HostProto.ToolDescriptor descriptor, string routeId)
    {
        var route = descriptor.Routes.FirstOrDefault(route =>
                        string.Equals(route.RouteId, routeId, StringComparison.OrdinalIgnoreCase))
                    ?? descriptor.Routes.FirstOrDefault()
                    ?? throw new InvalidDataException($"Tool '{descriptor.ToolId}' has no route.");
        var source = ResolveExternalSurfaceUri(descriptor, route);
        var isWebSurface = descriptor.ToolType == "web-surface" || route.SurfaceKind == "web";
        var effectiveToolType = isWebSurface ? "web-surface" : descriptor.ToolType;
        Func<Task>? launch = route.SurfaceKind == "native" && !string.IsNullOrWhiteSpace(route.Source)
            ? () => LaunchExternalAsync(route.Source)
            : null;
        IMptWebSurfaceSession? webSurfaceSession = null;
        var commands = descriptor.Commands
            .Where(command => !isWebSurface ||
                              !(command.Id.EndsWith(".refresh", StringComparison.OrdinalIgnoreCase) ||
                                command.Id.EndsWith(".open-external", StringComparison.OrdinalIgnoreCase)))
            .Select(command => new ExternalToolCommandViewModel(
                command.Title,
                command.Description,
                () => InvokeExternalToolCommandAsync(descriptor, route, command, CancellationToken.None),
                message => SetStatus($"{descriptor.Title}: {message}")))
            .ToArray();
        var viewModel = new ExternalSdkToolViewModel(
            descriptor.ToolId,
            descriptor.Title,
            descriptor.Description,
            effectiveToolType,
            string.IsNullOrWhiteSpace(route.Title) ? descriptor.Title : route.Title,
            source,
            route.OpenExternal,
            commands,
            descriptor.Settings?.ValuesPath,
            request => HandleExternalWebBridgeRequestAsync(descriptor, route, request),
            refresh: () =>
            {
                if (webSurfaceSession is not null)
                {
                    webSurfaceSession.Reload();
                    return Task.CompletedTask;
                }
                return ShowToolPageAsync(descriptor.ToolId, route.RouteId);
            },
            returnToTools: () => ShowPageAsync(ToolsPage),
            launch: launch);
        var view = new ExternalSdkToolView { DataContext = viewModel };

        if (isWebSurface)
        {
            try
            {
                if (source is null)
                {
                    throw new InvalidDataException("The web surface source is not configured or its static entry point is missing.");
                }
                if (_webSurfaceService is null)
                {
                    viewModel.ReportSurface("unavailable", "This Shell does not provide the Web Surface host capability.");
                }
                else
                {
                    webSurfaceSession = _webSurfaceService.CreateSession(CreateExternalWebSurfaceRequest(
                        descriptor,
                        route,
                        source,
                        (request, _) => HandleExternalWebBridgeRequestAsync(descriptor, route, request)));
                    viewModel.SetWebSurfaceSession(webSurfaceSession);
                    view.SetHostedSurface(webSurfaceSession.View);
                }
            }
            catch (Exception ex)
            {
                webSurfaceSession?.Dispose();
                webSurfaceSession = null;
                viewModel.ReportSurface("failed", ex.GetBaseException().Message);
            }
        }
        else if (descriptor.ToolType == "dotnet-surface")
        {
            try
            {
                var loadedSurface = CreateExternalDotnetSurface(descriptor, route);
                viewModel.SetOwnedSurface(loadedSurface);
                view.SetManagedSurface(loadedSurface.Control);
            }
            catch (Exception ex)
            {
                viewModel.ReportSurface("failed", ex.GetBaseException().Message);
            }
        }
        else if (descriptor.ToolType is "native-tool" or "headless-tool")
        {
            viewModel.ReportSurface("ready", "External runtime contract loaded.");
        }

        SetOwnedContent(_contentHost, view);
        SetStatus($"Opened {descriptor.Title} from {descriptor.SourceDirectory}.");
        await Task.CompletedTask;
    }

    internal static MptWebSurfaceRequest CreateExternalWebSurfaceRequest(
        HostProto.ToolDescriptor descriptor,
        HostProto.ToolRoute route,
        Uri source,
        Func<string, CancellationToken, Task<string>> handleBridgeRequestAsync)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handleBridgeRequestAsync);

        return new MptWebSurfaceRequest(
            descriptor.ToolId,
            route.RouteId,
            source,
            ResolveExternalAllowedOrigins(descriptor, route),
            handleBridgeRequestAsync);
    }

    private DotnetSurfaceLoader.LoadedSurface CreateExternalDotnetSurface(
        HostProto.ToolDescriptor descriptor,
        HostProto.ToolRoute route)
    {
        if (string.IsNullOrWhiteSpace(route.Assembly) || !File.Exists(route.Assembly))
        {
            throw new FileNotFoundException("Dotnet surface assembly was not found.", route.Assembly);
        }
        if (string.IsNullOrWhiteSpace(route.Type))
        {
            throw new InvalidDataException("Dotnet surface type is required.");
        }

        var dataDirectory = ResolveExternalToolDataDirectory(descriptor.ToolId);

        // Load into a collectible, shadow-copied AssemblyLoadContext so the surface can be unloaded
        // on refresh or removal without leaking assemblies into the default context.
        var context = new MptAvaloniaSurfaceContext(
            descriptor.ToolId,
            route.RouteId,
            dataDirectory,
            _appearance.CurrentTheme,
            async (commandId, args, cancellationToken) =>
            {
                var result = await ExecuteRuntimeCommandAsync(commandId, args, cancellationToken: cancellationToken);
                var success = result.State is "succeeded" or "success" or "ready";
                return new CommandExecutionResult(
                    Guid.NewGuid().ToString("N"),
                    commandId,
                    result.State,
                    success,
                    result.Message,
                    success ? null : new MptRuntimeError("command.failed", result.Message));
            },
            (toolId, targetRouteId, _) => ShowToolPageAsync(toolId, targetRouteId),
            new ScopedServiceUnitClient(_serviceManagerAdmin, descriptor.ToolId),
            entry => SetStatus($"[{entry.Level}] {entry.Message}"),
            callback => SubscribeSurfaceEvents(descriptor.OwnerModuleId, callback))
        {
            WebSurfaces = _webSurfaceService
       };
       try
       {
           if (_devSource.SyncOnRefresh)
           {
                var outcome = _devSource.SyncForToolAsync(descriptor.ToolId, enabledOnly: true).GetAwaiter().GetResult();
               if (outcome.UpdatedFiles > 0)
                {
                    SetStatus($"Synced {outcome.UpdatedFiles} file(s) from developer sources for {descriptor.ToolId}.");
                }
            }
        }
        catch (Exception devSourceEx)
        {
            SetStatus($"Developer source sync skipped: {devSourceEx.Message}");
        }
       return _dotnetSurfaceLoader.Load(descriptor, route, context);
    }

    private IDisposable SubscribeSurfaceEvents(string sourceId, Action<MptSurfaceEvent> callback)
    {
        Action<HostProto.HostEvent> bridge = evt =>
        {
            if (!string.Equals(evt.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var payload = JsonStructMapper.ToJsonObject(evt.Payload);
            payload["shellBridgeReceivedUtc"] = DateTimeOffset.UtcNow.ToString("O");
            callback(new MptSurfaceEvent(
                evt.Seq,
                evt.SourceId,
                evt.Type,
                evt.Time.ToDateTimeOffset(),
                payload));
        };
        _runnerEvents.HostEventReceived += bridge;
        return new CallbackDisposable(() => _runnerEvents.HostEventReceived -= bridge);
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? _callback = callback;

        public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
    }

    private static Uri? ResolveExternalSurfaceUri(HostProto.ToolDescriptor descriptor, HostProto.ToolRoute route)
    {
        var settings = LoadExternalToolSettings(descriptor);
        var sourceValue = ExpandExternalToolSettings(route.Source, settings);
        if (!string.IsNullOrWhiteSpace(sourceValue))
        {
            if (!Uri.TryCreate(sourceValue, UriKind.Absolute, out var source) ||
                source.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(source.UserInfo))
            {
                throw new InvalidDataException($"Tool '{descriptor.ToolId}' panel URL must be an absolute HTTP(S) URL.");
            }
            return source;
        }
        var staticRoot = ExpandExternalToolSettings(route.StaticRoot, settings);
        if (!string.IsNullOrWhiteSpace(staticRoot))
        {
            var path = Directory.Exists(staticRoot)
                ? Path.Combine(staticRoot, "index.html")
                : staticRoot;
            if (File.Exists(path))
            {
                return new Uri(Path.GetFullPath(path));
            }
        }
        return null;
    }

    private static IReadOnlyList<Uri> ResolveExternalAllowedOrigins(
        HostProto.ToolDescriptor descriptor,
        HostProto.ToolRoute route)
    {
        var settings = LoadExternalToolSettings(descriptor);
        var origins = new List<Uri>();
        foreach (var value in route.AllowedOrigins)
        {
            var expanded = ExpandExternalToolSettings(value, settings);
            if (!Uri.TryCreate(expanded, UriKind.Absolute, out var origin) ||
                (origin.Scheme is not ("http" or "https") && !origin.IsFile) ||
                !string.IsNullOrEmpty(origin.UserInfo))
            {
                throw new InvalidDataException($"Tool '{descriptor.ToolId}' declares an invalid allowed web origin.");
            }
            origins.Add(origin);
        }
        return origins;
    }

    private static IReadOnlyDictionary<string, string> LoadExternalToolSettings(HostProto.ToolDescriptor descriptor)
    {
        var path = descriptor.Settings?.ValuesPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject values)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        return values
            .Where(item => item.Value is JsonValue)
            .ToDictionary(
                item => item.Key,
                item => item.Value is JsonValue value && value.TryGetValue<string>(out var text)
                    ? text
                    : item.Value!.ToJsonString(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string ExpandExternalToolSettings(string value, IReadOnlyDictionary<string, string> settings)
    {
        return ExternalToolSettingToken.Replace(value ?? "", match =>
        {
            var name = match.Groups["name"].Value;
            if (!settings.TryGetValue(name, out var settingValue) || string.IsNullOrWhiteSpace(settingValue))
            {
                throw new InvalidDataException($"Required tool setting '{name}' is missing. Open Settings and configure it first.");
            }
            return settingValue;
        });
    }

    private static Task LaunchExternalAsync(string source)
    {
        Process.Start(new ProcessStartInfo(source) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private async Task<string> InvokeExternalToolCommandAsync(
        HostProto.ToolDescriptor descriptor,
        HostProto.ToolRoute route,
        HostProto.ToolCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Id.EndsWith(".open-external", StringComparison.OrdinalIgnoreCase))
        {
            var source = ResolveExternalSurfaceUri(descriptor, route) ?? throw new InvalidDataException("The external URL is not configured.");
            Process.Start(new ProcessStartInfo(source.AbsoluteUri) { UseShellExecute = true });
            return "Opened in the default browser.";
        }

        if (descriptor.Runtime is not null &&
            !string.IsNullOrWhiteSpace(descriptor.Runtime.Endpoint) &&
            !string.IsNullOrWhiteSpace(command.Path))
        {
            var settings = LoadExternalToolSettings(descriptor);
            var runtimeEndpoint = ExpandExternalToolSettings(descriptor.Runtime.Endpoint, settings);
            if (!Uri.TryCreate(runtimeEndpoint, UriKind.Absolute, out var runtimeUri) ||
                runtimeUri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(runtimeUri.UserInfo))
            {
                throw new InvalidDataException($"Tool '{descriptor.ToolId}' API endpoint must be an absolute HTTP(S) URL.");
            }
            var endpoint = runtimeUri.AbsoluteUri.TrimEnd('/') + "/" + command.Path.TrimStart('/');
            using var request = new HttpRequestMessage(
                new HttpMethod(string.IsNullOrWhiteSpace(command.Method) ? "POST" : command.Method),
                endpoint);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(100, descriptor.Runtime.TimeoutMs)));
            using var response = await ExternalToolHttpClient.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            response.EnsureSuccessStatusCode();
            return string.IsNullOrWhiteSpace(body)
                ? $"{command.Title} completed ({(int)response.StatusCode})."
                : body;
        }

        var result = await ExecuteRuntimeCommandAsync(command.Id, new JsonObject(), cancellationToken: cancellationToken);
        if (result.State is not ("succeeded" or "success" or "ready"))
        {
            throw new InvalidOperationException(result.Message);
        }
        return result.Message;
    }

}
