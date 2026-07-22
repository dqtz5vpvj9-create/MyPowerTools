using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MyPowerTools.Packaging;

/// <summary>
/// Single source of truth for the default values applied to "quick web panel" files —
/// single-file web-surface tools that declare as little as { "title", "url" }.
///
/// The quick path is NOT a parallel tool mechanism: every default below is the default
/// of a real schemas/tool.schema.json field. A panel file may write any real manifest
/// field back to override its default and restore the full web-surface capability
/// (commands, runtime, settings, allowedOrigins, ...). The normalized document always
/// passes the strict tool schema; the same folder can later be packed and shipped via
/// `mpt tool pack` without a rewrite.
///
/// The user file is never rewritten — defaults are applied in memory at load time.
/// </summary>
public static class WebSurfaceDefaults
{
    public const string SchemaVersion = "1.0";
    public const string ToolType = "web-surface";
    public const string Icon = "tool.external";
    public const string Category = "Custom panels";
    public const string Availability = "available";
    public const string PrimaryRouteId = "main";
    public const string RouteTitle = "Overview";
    public const string SurfaceKind = "web";
    public const bool SurfaceOpenExternal = true;
    public const string HomeCardPrimaryActionLabel = "Open";
    public const int HomeCardOrder = 500;

    /// <summary>Matches the toolId pattern in schemas/tool.schema.json.</summary>
    public static readonly Regex ToolIdPattern = new(
        @"^[a-z0-9][a-z0-9.-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Derives a schema-valid toolId from a panel file name stem:
    /// "My Panel" → "custom.my-panel". An explicit "toolId" in the file is used
    /// as-is (no prefix) and only pattern-validated.
    /// </summary>
    public static string DeriveToolIdFromFileName(string fileNameStem)
    {
        var sanitized = new string((fileNameStem ?? "")
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (sanitized.Contains("--", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("--", "-", StringComparison.Ordinal);
        }
        sanitized = sanitized.Trim('-');
        if (sanitized.Length == 0)
        {
            sanitized = "panel";
        }
        return "custom." + sanitized;
    }

    /// <summary>
    /// A document is a quick web panel when it declares a top-level "url" string and no
    /// "routes" array. ("url" is not part of the tool schema — once "routes" is present
    /// the file is a regular full manifest and is loaded as-is.)
    /// </summary>
    public static bool IsQuickPanelCandidate(JsonObject rawDocument)
    {
        ArgumentNullException.ThrowIfNull(rawDocument);
        if (rawDocument.ContainsKey("routes"))
        {
            return false;
        }
        return !string.IsNullOrWhiteSpace(GetString(rawDocument, "url"));
    }

    public static void ValidateUrl(string url, string sourceDescription)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new InvalidDataException(
                $"Quick web panel '{sourceDescription}' must declare an absolute http(s) \"url\"; got '{url}'.");
        }
    }

    /// <summary>
    /// Normalizes a quick-panel document into a full, strict-schema-valid web-surface
    /// manifest object. User-supplied fields win: nested objects merge deeply, arrays
    /// and scalars replace the defaults. The "url" shortcut is consumed into
    /// routes[0].surface.source and stripped from the result.
    /// </summary>
    public static JsonObject NormalizeQuickPanel(
        JsonObject rawDocument,
        string fileNameStem,
        string sourceDescription)
    {
        ArgumentNullException.ThrowIfNull(rawDocument);
        var url = GetString(rawDocument, "url")
            ?? throw new InvalidDataException(
                $"Quick web panel '{sourceDescription}' is missing its \"url\".");
        ValidateUrl(url, sourceDescription);

        var explicitToolId = GetString(rawDocument, "toolId");
        var toolId = string.IsNullOrWhiteSpace(explicitToolId)
            ? DeriveToolIdFromFileName(fileNameStem)
            : explicitToolId!;
        if (!ToolIdPattern.IsMatch(toolId))
        {
            throw new InvalidDataException(
                $"Quick web panel '{sourceDescription}' has an invalid toolId '{toolId}' " +
                $"(must match {ToolIdPattern}).");
        }

        var title = GetString(rawDocument, "title") ?? Humanize(fileNameStem);

        var defaults = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["toolId"] = toolId,
            ["title"] = title,
            ["description"] = $"Quick panel for {url}",
            ["icon"] = Icon,
            ["category"] = Category,
            ["type"] = ToolType,
            ["availability"] = Availability,
            ["primaryRouteId"] = PrimaryRouteId,
            ["routes"] = new JsonArray
            {
                new JsonObject
                {
                    ["routeId"] = PrimaryRouteId,
                    ["surfaceId"] = $"{toolId}.{PrimaryRouteId}",
                    ["title"] = RouteTitle,
                    ["surface"] = new JsonObject
                    {
                        ["kind"] = SurfaceKind,
                        ["source"] = url,
                        ["openExternal"] = SurfaceOpenExternal,
                        ["allowedOrigins"] = new JsonArray()
                    }
                }
            },
            ["homeCard"] = new JsonObject
            {
                ["summary"] = $"Open {title}",
                ["primaryActionLabel"] = HomeCardPrimaryActionLabel,
                ["order"] = HomeCardOrder
            },
            ["development"] = new JsonObject
            {
                ["loose"] = true
            }
        };

        // Merge the user document over the defaults (user wins), without the "url"
        // shortcut key, which is not part of the tool schema.
        var overrides = rawDocument.DeepClone().AsObject();
        overrides.Remove("url");
        return DeepMerge(defaults, overrides);
    }

    private static JsonObject DeepMerge(JsonObject defaults, JsonObject overrides)
    {
        var result = defaults.DeepClone().AsObject();
        foreach (var (key, value) in overrides)
        {
            if (value is JsonObject overrideObject &&
                result[key] is JsonObject existingObject)
            {
                result[key] = DeepMerge(existingObject, overrideObject);
            }
            else
            {
                // User arrays replace default arrays wholesale (writing "routes" takes
                // full ownership of the route list); user scalars win outright.
                result[key] = value is null ? null : value.DeepClone();
            }
        }
        return result;
    }

    private static string? GetString(JsonObject document, string key)
    {
        return document[key] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    private static string Humanize(string fileNameStem)
    {
        var words = (fileNameStem ?? "")
            .Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..])
            .ToArray();
        return words.Length == 0 ? "Panel" : string.Join(" ", words);
    }
}
