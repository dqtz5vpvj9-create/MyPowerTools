using ZXing;
using ZXing.Common;

namespace MyPowerTools.Android.Pairing;

/// <summary>
/// Turns one camera luminance frame into the text of a QR code, or into nothing.
/// <para>
/// Android-free on purpose: the camera lifecycle lives in <c>PairingQrScannerActivity</c>, and this
/// type only owns the decode rules, so the rules are unit tested on a build machine. The decoder
/// accepts QR only — a pairing link is never carried by any other symbology — and every failure is
/// a plain <see langword="null"/> because a camera produces unusable frames constantly (motion
/// blur, a hand, an empty wall) and that is not an error.
/// </para>
/// </summary>
public sealed class MptQrCodeDecoder
{
    private readonly BarcodeReaderGeneric _reader;
    private readonly object _gate = new();

    public MptQrCodeDecoder()
    {
        var options = new DecodingOptions
        {
            PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
            // QR is the only accepted symbology, so asking the detector for more work per frame
            // buys recognition of a dense code held at an angle — the normal case when a phone is
            // pointed at a laptop screen.
            TryHarder = true,
            CharacterSet = "UTF-8"
        };
        _reader = new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options = options
        };
    }

    /// <summary>
    /// Decodes a single-plane luminance frame. <paramref name="pixels"/> is the Y plane of a
    /// <c>YUV_420_888</c> image; <paramref name="rowStride"/> may exceed
    /// <paramref name="width"/> because camera rows are aligned, so both are required.
    /// </summary>
    /// <returns>The decoded text, or <see langword="null"/> when this frame holds no readable QR code.</returns>
    public string? Decode(byte[] pixels, int width, int height, int rowStride)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || rowStride < width)
        {
            return null;
        }

        var required = (long)rowStride * height;
        if (pixels.LongLength < required)
        {
            return null;
        }

        try
        {
            var cropped = rowStride == width ? pixels : CropStride(pixels, width, height, rowStride);
            var source = new PlanarYUVLuminanceSource(cropped, width, height, 0, 0, width, height, false);
            lock (_gate)
            {
                // The reader is not documented as thread safe and a scanner pipeline can deliver a
                // second frame before the first decode returned.
                return _reader.Decode(source)?.Text;
            }
        }
        catch (Exception)
        {
            // ReaderException and anything a malformed frame can throw all mean "no code in this
            // frame". Reporting them would flood the log with one entry per camera frame.
            return null;
        }
    }

    /// <summary>
    /// Decodes a frame that was captured with the sensor rotated to the display. Phone cameras
    /// always deliver their frames in sensor orientation, so a portrait scanner needs the buffer
    /// rotated before the QR locator can see the finder patterns.
    /// </summary>
    /// <param name="rotationDegrees">0, 90, 180 or 270, clockwise.</param>
    /// <param name="outWidth">Width of the rotated frame.</param>
    /// <param name="outHeight">Height of the rotated frame.</param>
    public string? DecodeRotated(byte[] pixels, int width, int height, int rowStride, int rotationDegrees, out int outWidth, out int outHeight)
    {
        var normalized = ((rotationDegrees % 360) + 360) % 360;
        switch (normalized)
        {
            case 90:
                outWidth = height;
                outHeight = width;
                return Decode(Rotate90(pixels, width, height, rowStride), outWidth, outHeight, outWidth);
            case 180:
                outWidth = width;
                outHeight = height;
                return Decode(Rotate180(pixels, width, height, rowStride), outWidth, outHeight, outWidth);
            case 270:
                outWidth = height;
                outHeight = width;
                return Decode(Rotate270(pixels, width, height, rowStride), outWidth, outHeight, outWidth);
            default:
                outWidth = width;
                outHeight = height;
                return Decode(pixels, width, height, rowStride);
        }
    }

    private static byte[] CropStride(byte[] pixels, int width, int height, int rowStride)
    {
        var cropped = new byte[width * height];
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(pixels, row * rowStride, cropped, row * width, width);
        }

        return cropped;
    }

    private static byte[] Rotate90(byte[] pixels, int width, int height, int rowStride)
    {
        var rotated = new byte[width * height];
        for (var row = 0; row < height; row++)
        {
            var source = row * rowStride;
            for (var column = 0; column < width; column++)
            {
                // Clockwise: the last source row becomes the first destination column.
                rotated[column * height + (height - 1 - row)] = pixels[source + column];
            }
        }

        return rotated;
    }

    private static byte[] Rotate180(byte[] pixels, int width, int height, int rowStride)
    {
        var rotated = new byte[width * height];
        var last = (width * height) - 1;
        for (var row = 0; row < height; row++)
        {
            var source = row * rowStride;
            for (var column = 0; column < width; column++)
            {
                rotated[last - (row * width + column)] = pixels[source + column];
            }
        }

        return rotated;
    }

    private static byte[] Rotate270(byte[] pixels, int width, int height, int rowStride)
    {
        var rotated = new byte[width * height];
        for (var row = 0; row < height; row++)
        {
            var source = row * rowStride;
            for (var column = 0; column < width; column++)
            {
                // Counter-clockwise: the first source row becomes the last destination column.
                rotated[(width - 1 - column) * height + row] = pixels[source + column];
            }
        }

        return rotated;
    }
}
