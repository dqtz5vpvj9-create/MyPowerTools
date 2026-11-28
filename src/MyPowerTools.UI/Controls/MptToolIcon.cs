using Avalonia;
using Avalonia.Controls;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;

namespace MyPowerTools.UI.Controls;

/// <summary>
/// Tinted vector tile for a tool, page or settings area. <see cref="IconKey"/> accepts a tool id
/// (for example <c>remote-notifications</c>) or a semantic key (for example <c>appearance</c>);
/// the control resolves it to one of the shared <c>MptIconGeo*</c> geometries and a <c>tint-*</c>
/// class. Size, padding, corner radius and colours all come from theme styles, so the tile follows
/// light/dark switches and density tokens without any literal values here.
/// </summary>
public class MptToolIcon : Border
{
    public static readonly StyledProperty<string?> IconKeyProperty =
        AvaloniaProperty.Register<MptToolIcon, string?>(nameof(IconKey));

    private static readonly string[] TintClasses =
    [
        "tint-blue", "tint-violet", "tint-green", "tint-amber",
        "tint-cyan", "tint-rose", "tint-indigo", "tint-slate"
    ];

    private readonly ShapePath _glyph = new()
    {
        Stretch = Stretch.Uniform,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round
    };

    private IDisposable? _geometryBinding;

    static MptToolIcon()
    {
        IconKeyProperty.Changed.AddClassHandler<MptToolIcon>((icon, _) => icon.ApplyIcon());
    }

    public MptToolIcon()
    {
        _glyph.Classes.Add("MptToolIconGlyph");
        Child = _glyph;
        ApplyIcon();
    }

    public string? IconKey
    {
        get => GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    /// <summary>Maps a tool id or semantic key to a geometry resource key and a tint name.</summary>
    public static (string Geometry, string Tint) Resolve(string? key)
    {
        var value = (key ?? string.Empty).Trim().ToLowerInvariant();
        static bool Has(string text, params string[] parts) => parts.Any(text.Contains);

        if (value.Length == 0) return ("MptIconGeoBolt", "blue");
        if (Has(value, "toolkit", "all-tools", "catalog")) return ("MptIconGeoGrid", "blue");
        if (Has(value, "notif", "inbox")) return ("MptIconGeoBell", "violet");
        if (Has(value, "command", "terminal", "shell-run")) return ("MptIconGeoTerminal", "green");
        if (Has(value, "process", "monitor-process")) return ("MptIconGeoActivity", "cyan");
        if (Has(value, "adb", "forward")) return ("MptIconGeoSmartphone", "indigo");
        if (Has(value, "screenease", "screen", "display", "eye")) return ("MptIconGeoSun", "amber");
        if (Has(value, "doubao", "agent", "computer-use", "assistant")) return ("MptIconGeoSparkles", "violet");
        if (Has(value, "smartbird", "thermo", "temperature", "climate")) return ("MptIconGeoThermometer", "rose");
        if (Has(value, "input", "keyboard", "ime", "shortcut", "hotkey")) return ("MptIconGeoKeyboard", "indigo");
        if (Has(value, "paste", "image", "clipboard", "screenshot")) return ("MptIconGeoImage", "rose");
        if (Has(value, "transfer", "file", "sync")) return ("MptIconGeoTransfer", "blue");
        if (Has(value, "nssm", "service", "server", "system", "maintenance")) return ("MptIconGeoServer", "slate");
        if (Has(value, "lag", "clean", "performance", "speed")) return ("MptIconGeoGauge", "amber");
        if (Has(value, "web", "browser", "http", "site")) return ("MptIconGeoGlobe", "blue");
        if (Has(value, "appearance", "theme", "palette")) return ("MptIconGeoPalette", "violet");
        if (Has(value, "background", "tray", "startup")) return ("MptIconGeoLaptop", "slate");
        if (Has(value, "developer", "source", "code", "debug")) return ("MptIconGeoCode", "cyan");
        if (Has(value, "refresh", "update")) return ("MptIconGeoRefresh", "blue");
        if (Has(value, "android", "phone", "mobile")) return ("MptIconGeoSmartphone", "green");
        return ("MptIconGeoBolt", "blue");
    }

    private void ApplyIcon()
    {
        var (geometry, tint) = Resolve(IconKey);
        var activeTint = "tint-" + tint;
        foreach (var name in TintClasses)
        {
            Classes.Set(name, string.Equals(name, activeTint, StringComparison.Ordinal));
        }

        _geometryBinding?.Dispose();
        _geometryBinding = _glyph.Bind(ShapePath.DataProperty, _glyph.GetResourceObservable(geometry));
    }
}
