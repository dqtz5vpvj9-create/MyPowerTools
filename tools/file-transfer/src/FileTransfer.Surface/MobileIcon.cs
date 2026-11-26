using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Controls.Shapes;

namespace FileTransfer.Surface;

/// <summary>
/// One stroke icon from the shared mobile theme's icon dictionary.
///
/// The theme's geometries are stroke outlines (the prototype's SVGs are <c>fill:none;stroke:…</c>),
/// and the theme styles them through <c>Path.MptMobileIcon</c> and friends. So this is an
/// <see cref="Avalonia.Controls.Shapes.Path"/>, not a <see cref="PathIcon"/>: a PathIcon fills its
/// geometry, which turns a stroked outline into a solid block, and its template ignores the shared
/// stroke styling.
///
/// The geometry is resolved twice on purpose. At construction the control is not in a tree yet, so
/// <see cref="StyledElement.TryFindResource(object?, out object?)"/> finds nothing; the icon therefore
/// resolves again from the nearest resource host as soon as it is attached. That is what makes a
/// toolbar button show a real glyph instead of an empty square.
/// </summary>
internal sealed class MobileIcon : Avalonia.Controls.Shapes.Path
{
    private string? _iconKey;

    public MobileIcon() => Stretch = Avalonia.Media.Stretch.None;

    /// <summary>The shared dictionary key, for example <c>MptMobileIconPlus</c>.</summary>
    public string? IconKey
    {
        get => _iconKey;
        set
        {
            _iconKey = value;
            Resolve();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The tree is the first moment a theme resource can actually be found.
        Resolve();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // The theme variant can change while this tool is closed, so the next attach resolves again
        // instead of keeping a geometry and a stroke from the previous variant.
        Reset();
    }

    /// <summary>
    /// Re-resolves when the theme variant changes at runtime: a dark page must not keep the light
    /// palette's near-black stroke, which would make the icon invisible.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ThemeVariantScope.ActualThemeVariantProperty) return;
        Reset();
        Resolve();
    }

    private void Reset()
    {
        Data = null;
        Stroke = null;
        StrokeThickness = 0;
    }

    /// <summary>
    /// Resolves geometry and stroke. It runs on attach, because that is the first moment the theme
    /// resources are reachable: a control created in code has no ancestors yet, so the shared
    /// dictionaries — which live on the window and the application — cannot be found before then.
    /// </summary>
    private void Resolve()
    {
        if (_iconKey is not { Length: > 0 } key) return;
        ApplyStroke();
        if (this.TryFindResource(key, out var resource) && resource is Geometry geometry)
        {
            Data = geometry;
            return;
        }
        foreach (var ancestor in this.GetVisualAncestors())
        {
            if (ancestor is not IResourceHost host) continue;
            if (host.TryGetResource(key, ActualThemeVariant, out var found) && found is Geometry hostGeometry)
            {
                Data = hostGeometry;
                return;
            }
        }
    }

    /// <summary>
    /// A stroked outline with no stroke paints nothing, so the stroke is set from the theme's own
    /// brush once, with the theme's width; the shared Path style then overrides it when it applies.
    /// </summary>
    private void ApplyStroke()
    {
        if (Stroke is not null && StrokeThickness > 0) return;
        Stroke = FindThemeBrush("MptMobileTextBrush") ?? Brushes.Black;
        StrokeThickness = FindThemeWidth("MptMobileIconStrokeThickness") ?? 1.65;
        Fill = null;
    }

    /// <summary>
    /// Resolves a brush from the shared theme. The palette lives in theme dictionaries, so the lookup
    /// has to name a concrete variant: querying with the ambient or default variant finds nothing even
    /// though the styled text next to the icon resolved the same brush.
    /// </summary>
    private IBrush? FindThemeBrush(string key)
    {
        foreach (var variant in WantedVariants())
            if (FindResource(key, variant) is IBrush brush)
                return brush;
        return null;
    }

    private double? FindThemeWidth(string key)
    {
        foreach (var variant in WantedVariants())
            if (FindResource(key, variant) is double width)
                return width;
        return null;
    }

    /// <summary>The variant this page should read, then the two concrete ones as a fallback.</summary>
    private IEnumerable<ThemeVariant> WantedVariants()
    {
        var requested = RequestedVariant();
        if (requested is not null) yield return requested;
        if (ActualThemeVariant != ThemeVariant.Default) yield return ActualThemeVariant;
        yield return ThemeVariant.Light;
        yield return ThemeVariant.Dark;
    }

    private ThemeVariant? RequestedVariant()
    {
        foreach (var node in this.GetSelfAndVisualAncestors())
        {
            if (node is ThemeVariantScope scope && scope.RequestedThemeVariant != ThemeVariant.Default) return scope.RequestedThemeVariant;
            if (node is TopLevel top && top.RequestedThemeVariant != ThemeVariant.Default) return top.RequestedThemeVariant;
        }
        return Application.Current?.RequestedThemeVariant is { } app && app != ThemeVariant.Default ? app : null;
    }

    /// <summary>The first host that answers for this key and variant wins; the icon's own scope comes first.</summary>
    private object? FindResource(string key, ThemeVariant variant)
    {
        if (this.TryGetResource(key, variant, out var local)) return local;
        foreach (var ancestor in this.GetVisualAncestors())
            if (ancestor is IResourceHost host && host.TryGetResource(key, variant, out var found))
                return found;
        return Application.Current is { } app && app.TryGetResource(key, variant, out var appValue) ? appValue : null;
    }
}
