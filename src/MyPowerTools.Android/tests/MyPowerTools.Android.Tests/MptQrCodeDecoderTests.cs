using System.Reflection;
using MyPowerTools.Android.Pairing;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace MyPowerTools.Android.Tests;

public sealed class MptQrCodeDecoderTests
{
    private const string Payload = "mpt://pair/eyJEZXZpY2VJZCI6ImRldi0xIiwiTmFtZSI6IuW3peS9nOeUteiEkyIsIkFkZHJlc3MiOiIxMDAuNjQuMC41IiwiVG9rZW4iOiJzM2NyZXQtcmVjZWl2ZXItdG9rZW4tMDEyMzQ1Njc4OSJ9";

    [Fact]
    public void A_generated_qr_code_is_read_back_exactly()
    {
        var (luminance, width, height) = RenderQr(Payload);
        var decoder = new MptQrCodeDecoder();

        Assert.Equal(Payload, decoder.Decode(luminance, width, height, width));
    }

    [Fact]
    public void A_frame_without_a_code_is_not_an_error()
    {
        var decoder = new MptQrCodeDecoder();
        var blank = new byte[64 * 64];

        Assert.Null(decoder.Decode(blank, 64, 64, 64));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-4, 64, 64)]
    [InlineData(64, 0, 64)]
    [InlineData(64, 64, 32)]
    public void A_malformed_frame_is_rejected_instead_of_throwing(int width, int height, int stride)
    {
        var decoder = new MptQrCodeDecoder();
        Assert.Null(decoder.Decode(new byte[8], width, height, stride));
    }

    [Fact]
    public void A_stride_padded_frame_is_cropped_before_decoding()
    {
        var (luminance, width, height) = RenderQr(Payload);
        const int padding = 37;
        var padded = new byte[(width + padding) * height];
        for (var row = 0; row < height; row++)
        {
            // Garbage in the padding must not reach the decoder; only the visible row is copied.
            Array.Fill(padded, (byte)0x7F, row * (width + padding) + width, padding);
            Buffer.BlockCopy(luminance, row * width, padded, row * (width + padding), width);
        }

        var decoder = new MptQrCodeDecoder();
        Assert.Equal(Payload, decoder.Decode(padded, width, height, width + padding));
    }

    [Fact]
    public void A_portrait_frame_is_rotated_before_decoding()
    {
        // The camera always delivers its frames in sensor orientation, so a portrait scanner has to
        // rotate them itself. This is the round trip: rotate the rendered code, then decode it.
        var (luminance, width, height) = RenderQr(Payload);
        var decoder = new MptQrCodeDecoder();

        foreach (var rotation in new[] { 90, 180, 270 })
        {
            var rotated = Rotate(luminance, width, height, width, rotation);
            var (rotatedWidth, rotatedHeight) = rotation is 90 or 270 ? (height, width) : (width, height);
            var text = decoder.DecodeRotated(rotated, rotatedWidth, rotatedHeight, rotatedWidth, rotation,
                out var outWidth, out var outHeight);

            Assert.Equal(rotatedWidth, outWidth);
            Assert.Equal(rotatedHeight, outHeight);
            Assert.Equal(Payload, text);
        }
    }

    [Fact]
    public void Rotation_moves_each_pixel_to_its_documented_corner()
    {
        // A 3x2 frame with a unique value per pixel makes a wrong rotation direction visible.
        var source = new byte[] { 1, 2, 3, 4, 5, 6 };

        Assert.Equal(new byte[] { 4, 1, 5, 2, 6, 3 }, Rotate(source, 3, 2, 3, 90));
        Assert.Equal(new byte[] { 6, 5, 4, 3, 2, 1 }, Rotate(source, 3, 2, 3, 180));
        Assert.Equal(new byte[] { 3, 6, 2, 5, 1, 4 }, Rotate(source, 3, 2, 3, 270));
    }

    /// <summary>Renders a QR code and returns its luminance plane (Y of YUV, which is grey).</summary>
    private static (byte[] Luminance, int Width, int Height) RenderQr(string content)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions
            {
                Width = 360,
                Height = 360,
                Margin = 2,
                CharacterSet = "UTF-8",
                ErrorCorrection = ZXing.QrCode.Internal.ErrorCorrectionLevel.M
            }
        };

        var pixels = writer.Write(content);
        var luminance = new byte[pixels.Width * pixels.Height];
        for (var index = 0; index < luminance.Length; index++)
        {
            // BGRA source; a QR code is black or white, so any channel is the luminance.
            luminance[index] = pixels.Pixels[index * 4];
        }

        return (luminance, pixels.Width, pixels.Height);
    }

    private static byte[] Rotate(byte[] pixels, int width, int height, int rowStride, int degrees)
    {
        var method = typeof(MptQrCodeDecoder).GetMethod(
            degrees switch
            {
                90 => "Rotate90",
                180 => "Rotate180",
                _ => "Rotate270"
            },
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (byte[])method!.Invoke(null, [pixels, width, height, rowStride])!;
    }
}
