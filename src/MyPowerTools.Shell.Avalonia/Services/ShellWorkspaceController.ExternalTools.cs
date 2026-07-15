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
using MyPowerTools.Platform.Windows;
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
        Func<Task>? launch = route.SurfaceKind == "native" && !string.IsNullOrWhiteSpace(route.Source)
            ? () => LaunchExternalAsync(route.Source)
            : null;
        ExternalSdkToolViewModel? viewModel = null;
        ExternalSdkToolView? view = null;
        var commands = descriptor.Commands
            .Where(command => descriptor.ToolType != "web-surface" ||
                              !(command.Id.EndsWith(".refresh", StringComparison.OrdinalIgnoreCase) ||
                                command.Id.EndsWith(".open-external", StringComparison.OrdinalIgnoreCase)))
            .Select(command => new ExternalToolCommandViewModel(
            command.Title,
            command.Description,
            async () =>
            {
                try
                {
                    var message = await InvokeExternalToolCommandAsync(descriptor, route, command, CancellationToken.None);
                    viewModel?.ReportSurface("ready", message);
                    SetStatus($"{descriptor.Title}: {message}");
                }
                catch (Exception ex)
                {
                    viewModel?.ReportSurface("failed", ex.GetBaseException().Message);
                    SetStatus($"{descriptor.Title}: {ex.GetBaseException().Message}");
                }
            })).ToArray();
        viewModel = new ExternalSdkToolViewModel(
            descriptor.ToolId,
            descriptor.Title,
            descriptor.Description,
            descriptor.ToolType,
            string.IsNullOrWhiteSpace(route.Title) ? descriptor.Title : route.Title,
            source,
            route.OpenExternal,
            commands,
            descriptor.Settings?.ValuesPath,
            request => HandleExternalWebBridgeRequestAsync(descriptor, route, request),
            refresh: () =>
            {
                if (view is not null && descriptor.ToolType == "web-surface")
                {
                    view.ReloadWebSurface();
                    return Task.CompletedTask;
                }
                return ShowToolPageAsync(descriptor.ToolId, route.RouteId);
            },
            returnToTools: () => ShowPageAsync(ToolsPage),
            launch: launch);
        view = new ExternalSdkToolView { DataContext = viewModel };

        if (descriptor.ToolType == "dotnet-surface")
        {
            try
            {
                view.SetManagedSurface(CreateExternalDotnetSurface(descriptor, route));
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

    private Control CreateExternalDotnetSurface(HostProto.ToolDescriptor descriptor, HostProto.ToolRoute route)
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
        return _dotnetSurfaceLoader.Load(descriptor, route, new MptAvaloniaSurfaceContext(
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
            entry => SetStatus($"[{entry.Level}] {entry.Message}"))).Control;
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
                : body.Length <= 240 ? body : body[..240] + "…";
        }

        var result = await ExecuteRuntimeCommandAsync(command.Id, new JsonObject(), cancellationToken: cancellationToken);
        if (result.State is not ("succeeded" or "success" or "ready"))
        {
            throw new InvalidOperationException(result.Message);
        }
        return result.Message;
    }

    private async Task<string> HandleExternalWebBridgeRequestAsync(
        HostProto.ToolDescriptor descriptor,
        HostProto.ToolRoute route,
        string requestJson)
    {
        using var document = JsonDocument.Parse(requestJson);
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? "" : "";
        var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() ?? "" : "";
        try
        {
            JsonNode? result = type switch
            {
                "command.invoke" => await InvokeBridgeCommandAsync(descriptor, route, root),
                "settings.get" => ReadBridgeSetting(descriptor, root),
                "settings.set" => WriteBridgeSetting(descriptor, root),
                "secrets.get" => await ReadBridgeSecretAsync(descriptor, root),
                "secrets.set" => await WriteBridgeSecretAsync(descriptor, root),
                "navigation.openExternal" => OpenBridgeExternal(root),
                "event.publish" => await PublishBridgeEventAsync(descriptor, root),
                _ => throw new InvalidOperationException($"Unsupported WebBridge request '{type}'.")
            };
            return new JsonObject
            {
                ["version"] = "1.0",
                ["id"] = id,
                ["type"] = type + ".result",
                ["payload"] = result
            }.ToJsonString();
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["version"] = "1.0",
                ["id"] = id,
                ["type"] = type + ".result",
                ["error"] = new JsonObject
                {
                    ["code"] = "bridge.request.failed",
                    ["message"] = ex.GetBaseException().Message
                }
            }.ToJsonString();
        }
    }

    private async Task<JsonNode?> InvokeBridgeCommandAsync(HostProto.ToolDescriptor descriptor, HostProto.ToolRoute route, JsonElement root)
    {
        var payload = root.GetProperty("payload");
        var commandId = payload.GetProperty("commandId").GetString() ?? throw new InvalidDataException("commandId is required.");
        var command = descriptor.Commands.FirstOrDefault(item => string.Equals(item.Id, commandId, StringComparison.OrdinalIgnoreCase))
                      ?? throw new KeyNotFoundException($"Command '{commandId}' is not declared by {descriptor.ToolId}.");
        return JsonValue.Create(await InvokeExternalToolCommandAsync(descriptor, route, command, CancellationToken.None));
    }

    private static JsonNode? ReadBridgeSetting(HostProto.ToolDescriptor descriptor, JsonElement root)
    {
        var name = root.GetProperty("payload").GetProperty("name").GetString() ?? "";
        var values = LoadBridgeSettings(descriptor);
        return values[name]?.DeepClone();
    }

    private static JsonNode? WriteBridgeSetting(HostProto.ToolDescriptor descriptor, JsonElement root)
    {
        var payload = root.GetProperty("payload");
        var name = payload.GetProperty("name").GetString() ?? throw new InvalidDataException("Setting name is required.");
        var values = LoadBridgeSettings(descriptor);
        values[name] = JsonNode.Parse(payload.GetProperty("value").GetRawText());
        var path = descriptor.Settings?.ValuesPath ?? throw new InvalidDataException("Tool settings are not configured.");
        File.WriteAllText(path, values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return null;
    }

    private static JsonObject LoadBridgeSettings(HostProto.ToolDescriptor descriptor)
    {
        var path = descriptor.Settings?.ValuesPath ?? throw new InvalidDataException("Tool settings are not configured.");
        return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject values ? values : new JsonObject();
    }

    private static async Task<JsonNode?> ReadBridgeSecretAsync(HostProto.ToolDescriptor descriptor, JsonElement root)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The current platform Secret Store is unavailable.");
        }
        var name = ValidateBridgeSecretName(descriptor, root);
        var value = await new WindowsPlatformPack().Secrets.ReadAsync(SecretReference.Create(descriptor.ToolId, name), CancellationToken.None);
        return value is null ? null : JsonValue.Create(value);
    }

    private static async Task<JsonNode?> WriteBridgeSecretAsync(HostProto.ToolDescriptor descriptor, JsonElement root)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The current platform Secret Store is unavailable.");
        }
        var name = ValidateBridgeSecretName(descriptor, root);
        var value = root.GetProperty("payload").GetProperty("value").GetString() ?? "";
        await new WindowsPlatformPack().Secrets.SaveAsync(descriptor.ToolId, name, value, CancellationToken.None);
        return null;
    }

    private static string ValidateBridgeSecretName(HostProto.ToolDescriptor descriptor, JsonElement root)
    {
        var name = root.GetProperty("payload").GetProperty("name").GetString() ?? "";
        if (descriptor.Settings is null || !descriptor.Settings.Secrets.Contains(name))
        {
            throw new UnauthorizedAccessException($"Secret '{name}' is not declared by {descriptor.ToolId}.");
        }
        return name;
    }

    private static JsonNode? OpenBridgeExternal(JsonElement root)
    {
        var value = root.GetProperty("payload").GetProperty("url").GetString() ?? "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("External navigation requires an HTTP(S) URL without embedded credentials.");
        }
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return null;
    }

    private async Task<JsonNode?> PublishBridgeEventAsync(HostProto.ToolDescriptor descriptor, JsonElement root)
    {
        var payload = root.GetProperty("payload");
        var topic = payload.TryGetProperty("type", out var topicNode) ? topicNode.GetString() ?? "event" : "event";
        var eventPayload = payload.TryGetProperty("payload", out var eventNode) && eventNode.ValueKind == JsonValueKind.Object
            ? eventNode.GetRawText()
            : "{}";
        using var client = HostControlClient.ForDefaultEndpoint();
        var published = await client.PublishToolEventAsync(descriptor.ToolId, topic, eventPayload);
        SetStatus($"{descriptor.Title}: {topic} (event {published.EventSeq})");
        return JsonValue.Create(published.EventSeq);
    }
}
