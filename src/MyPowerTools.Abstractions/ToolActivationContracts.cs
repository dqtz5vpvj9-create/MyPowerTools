using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyPowerTools.Abstractions;

/// <summary>
/// Describes an external activation that targets one dynamically loaded tool surface.
/// The activation URI remains opaque to the host and is interpreted by the target tool.
/// </summary>
public sealed record ToolActivationRequest(
    string ToolId,
    string RouteId,
    string ActivationUri)
{
    /// <summary>
    /// Keeps the Shell window in its current presentation state while the target surface
    /// handles the activation. This supports tools that present their own top-level window.
    /// </summary>
    public bool SuppressShellWindow { get; init; }
}

/// <summary>
/// Stable command-line envelope used by product-owned protocol handlers to activate a tool
/// without introducing product identifiers into the Shell.
/// </summary>
public static class ToolActivationProtocol
{
    public const string ArgumentName = "--surface-activation";
    public const string ProductScheme = "mypowertools";
    public const string ProductActivationHost = "activate";

    private const int MaximumIdentifierLength = 128;
    private const int MaximumActivationUriLength = 16 * 1024;
    private const int MaximumPayloadLength = 24 * 1024;

    public static string Serialize(ToolActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = Normalize(request)
            ?? throw new ArgumentException("The tool activation request is invalid.", nameof(request));
        return JsonSerializer.Serialize(
            normalized,
            ToolActivationJsonContext.Default.ToolActivationRequest);
    }

    public static ToolActivationRequest? Parse(IEnumerable<string>? arguments)
    {
        var values = arguments?.ToArray() ?? [];
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index]?.Trim() ?? "";
            if (string.Equals(value, ArgumentName, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1 < values.Length ? Deserialize(values[index + 1]) : null;
            }

            var prefix = ArgumentName + "=";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Deserialize(value[prefix.Length..]);
            }
        }

        return null;
    }

    public static Uri CreateProductActivationUri(ToolActivationRequest request)
    {
        var payload = Uri.EscapeDataString(Serialize(request));
        return new Uri($"{ProductScheme}://{ProductActivationHost}?payload={payload}");
    }

    public static ToolActivationRequest? ParseProductActivationUri(string? value)
    {
        if (!Uri.TryCreate((value ?? "").Trim().Trim('"'), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, ProductScheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, ProductActivationHost, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var component in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = component.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], "payload", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Deserialize(Uri.UnescapeDataString(pair[1]));
                }
                catch (UriFormatException)
                {
                    return null;
                }
            }
        }
        return null;
    }

    public static ToolActivationRequest? Deserialize(string? payload)
    {
        var value = (payload ?? "").Trim();
        if (value.Length == 0 || value.Length > MaximumPayloadLength)
        {
            return null;
        }

        try
        {
            return Normalize(JsonSerializer.Deserialize(
                value,
                ToolActivationJsonContext.Default.ToolActivationRequest));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ToolActivationRequest? Normalize(ToolActivationRequest? request)
    {
        if (request is null)
        {
            return null;
        }

        var toolId = (request.ToolId ?? "").Trim();
        var routeId = (request.RouteId ?? "").Trim();
        var activationUri = (request.ActivationUri ?? "").Trim().Trim('"');
        if (!IsIdentifier(toolId) ||
            (routeId.Length > 0 && !IsIdentifier(routeId)) ||
            activationUri.Length == 0 ||
            activationUri.Length > MaximumActivationUriLength ||
            !Uri.TryCreate(activationUri, UriKind.Absolute, out _))
        {
            return null;
        }

        return new ToolActivationRequest(toolId, routeId, activationUri)
        {
            SuppressShellWindow = request.SuppressShellWindow
        };
    }

    private static bool IsIdentifier(string value)
    {
        return value.Length is > 0 and <= MaximumIdentifierLength &&
               value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }
}

[JsonSerializable(typeof(ToolActivationRequest))]
internal sealed partial class ToolActivationJsonContext : JsonSerializerContext;
