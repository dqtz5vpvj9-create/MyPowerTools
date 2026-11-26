using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace MyPowerTools.AvaloniaSdk.Controls;

/// <summary>
/// Draws a connection code (<c>mpt://pair/…</c>, <c>mpt://cloud/…</c>, <c>mpt://control/…</c>) as a
/// scannable QR symbol on a native Avalonia surface.
/// <para>
/// The control encodes <see cref="Value"/> verbatim. A connection code carries its receiver or
/// grant secret inside the payload, so nothing here shortens, rewrites or re-encodes the text
/// before it reaches the symbol, and the value must not be routed into logs, diagnostics,
/// automation names, window titles or crash reports. Show the control only after the user
/// explicitly asked for the connection code; it is a credential, not decoration.
/// </para>
/// <para>
/// Rendering rules: pure black modules on an opaque white margin that is four modules wide (the
/// quiet zone the QR specification requires). That palette is deliberate and does not follow the
/// theme — on a dark page the symbol is still a white square with black modules, because a camera
/// needs contrast rather than the app's colours. Module edges land on whole device pixels so the
/// symbol stays crisp at fractional display scaling, and a container too small to give every
/// module one device pixel draws nothing instead of an unscannable smear.
/// </para>
/// <para>
/// The control never grows past its container: give it at least
/// <see cref="RecommendedDisplaySize"/> of square space and bind <see cref="Value"/>. A null,
/// empty or unencodable value clears the surface; an oversized code is a silent blank, never an
/// exception on a settings page.
/// </para>
/// </summary>
/// <example>
/// <code>
/// &lt;controls:MptQrCode Value="{Binding PairingCode}" Width="240" Height="240" /&gt;
/// </code>
/// </example>
public sealed class MptQrCode : Control
{
    /// <summary>
    /// Square size a host should offer the control. Phones scan a code on another screen, so a
    /// smaller symbol is still correct but takes longer to lock on; the control itself only uses
    /// the space its container grants.
    /// </summary>
    public const double RecommendedDisplaySize = 240;

    /// <summary>White margin the QR specification requires around the symbol, in modules.</summary>
    private const int QuietZoneModules = 4;

    /// <summary>Identifies the <see cref="Value"/> property.</summary>
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<MptQrCode, string?>(nameof(Value));

    // Black and white are constants on purpose: they are the symbology's contrast, not the app
    // palette, and a themed brush could make a dark page produce an unscannable symbol.
    private static readonly IBrush ModuleBrush = Brushes.Black;
    private static readonly IBrush QuietZoneBrush = Brushes.White;

    private BitMatrix? _matrix;
    private string? _encodedValue;

    static MptQrCode()
    {
        AffectsRender<MptQrCode>(ValueProperty);
    }

    public MptQrCode()
    {
        // The module edges are already aligned to device pixels; smoothing them anyway would put a
        // grey fringe around every module, which is exactly what costs a camera its lock.
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    /// <summary>
    /// The connection code to display, exactly as it will be encoded. Bind the full code — a code
    /// that lost characters or the token part of its payload no longer pairs anything.
    /// </summary>
    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        // A QR symbol is square, so the shorter container side wins: a container that constrains
        // only one axis (a StackPanel, where height is infinite) still bounds the square by its
        // width. Only a fully unconstrained measure, or a collapsed parent, falls back to the
        // recommended display size.
        var side = Math.Min(availableSize.Width, availableSize.Height);
        if (!double.IsFinite(side) || side <= 0)
        {
            side = RecommendedDisplaySize;
        }

        return new Size(side, side);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (!TryGetMatrix(out var matrix))
        {
            return;
        }

        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (!(side > 0))
        {
            return;
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        if (!(scaling > 0))
        {
            scaling = 1d;
        }

        var modules = Math.Max(matrix.Width, matrix.Height);
        var deviceSide = (int)Math.Floor(side * scaling);
        if (modules <= 0 || deviceSide < modules)
        {
            // Fewer than one device pixel per module cannot be scanned, and drawing it would only
            // produce a grey square that looks like a code but never resolves.
            return;
        }

        // Whole device pixels per module: the symbol is scaled here rather than by the renderer,
        // so no module edge is ever split across two pixels.
        var modulePixels = deviceSide / modules;
        var symbolSide = modulePixels * modules / scaling;
        // Centre the symbol and keep its first edge on a device pixel boundary as well.
        var originX = Math.Round((Bounds.Width - symbolSide) / 2 * scaling) / scaling;
        var originY = Math.Round((Bounds.Height - symbolSide) / 2 * scaling) / scaling;
        var moduleSide = modulePixels / scaling;

        context.FillRectangle(QuietZoneBrush, new Rect(originX, originY, symbolSide, symbolSide));

        for (var row = 0; row < matrix.Height; row++)
        {
            var column = 0;
            while (column < matrix.Width)
            {
                if (!matrix[column, row])
                {
                    column++;
                    continue;
                }

                var start = column;
                while (column < matrix.Width && matrix[column, row])
                {
                    column++;
                }

                // One rectangle per run of dark modules: fewer edges to rasterise and no seam
                // where two neighbouring modules touch.
                context.DrawRectangle(ModuleBrush, null, new Rect(
                    originX + (start * moduleSide),
                    originY + (row * moduleSide),
                    (column - start) * moduleSide,
                    moduleSide));
            }
        }
    }

    /// <summary>
    /// Returns the module matrix for the current value, encoding it once per distinct value.
    /// </summary>
    private bool TryGetMatrix(out BitMatrix matrix)
    {
        var value = Value;
        if (string.IsNullOrEmpty(value))
        {
            Forget();
            matrix = null!;
            return false;
        }

        if (_matrix is not null && string.Equals(_encodedValue, value, StringComparison.Ordinal))
        {
            matrix = _matrix;
            return true;
        }

        try
        {
            var hints = new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.CHARACTER_SET] = "UTF-8",
                // Medium recovery is the QR default and keeps the module count (and therefore the
                // module size at a fixed display size) low enough for a phone camera.
                [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M,
                [EncodeHintType.MARGIN] = QuietZoneModules
            };

            // Requesting a 1x1 symbol keeps ZXing at one pixel per module; the control applies its
            // own whole-pixel scale afterwards instead of letting the encoder resample.
            var encoded = new QRCodeWriter().encode(value, BarcodeFormat.QR_CODE, 1, 1, hints);
            if (encoded is null || encoded.Width <= 0 || encoded.Height <= 0)
            {
                Forget();
                matrix = null!;
                return false;
            }

            _matrix = encoded;
            _encodedValue = value;
            matrix = encoded;
            return true;
        }
        catch (Exception)
        {
            // A payload that cannot fit a QR symbol, or a character set the encoder refuses,
            // leaves the surface blank. This control sits on pairing and settings pages, where an
            // exception would take the page down over a value the user cannot repair from here.
            Forget();
            matrix = null!;
            return false;
        }
    }

    private void Forget()
    {
        // Dropping the cached symbol when the value goes away keeps a stale credential from
        // outliving the page that showed it.
        _matrix = null;
        _encodedValue = null;
    }
}
