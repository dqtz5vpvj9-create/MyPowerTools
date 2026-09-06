using System.Runtime.InteropServices;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Mac;

public sealed class MacPasteboardImageService : IClipboardImageService
{
    private const int NoImage = 1;

    public Task<ClipboardImagePayload> ReadPngAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("NSPasteboard requires macOS.");
        }

        int status;
        nint bytes;
        nuint length;
        int width;
        int height;
        try
        {
            status = MacNative.ReadPasteboardPng(out bytes, out length, out width, out height);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(MacNative.MissingLibraryMessage, ex);
        }

        try
        {
            if (status == NoImage)
            {
                throw new InvalidOperationException("No image is available in the clipboard.");
            }
            if (status != 0)
            {
                throw new ExternalException($"NSPasteboard image conversion failed with status {status}.");
            }
            if (bytes == 0 || length == 0 || length > int.MaxValue || width <= 0 || height <= 0)
            {
                throw new InvalidDataException("NSPasteboard returned an invalid PNG payload.");
            }

            var png = new byte[(int)length];
            Marshal.Copy(bytes, png, 0, png.Length);
            return Task.FromResult(new ClipboardImagePayload(png, width, height));
        }
        finally
        {
            if (bytes != 0)
            {
                MacNative.Free(bytes);
            }
        }
    }

    public Task WriteTextAsync(string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            return Task.FromException(
                new PlatformNotSupportedException("NSPasteboard requires macOS."));
        }

        int status;
        try
        {
            status = MacNative.WritePasteboardText(value);
        }
        catch (DllNotFoundException ex)
        {
            return Task.FromException(
                new InvalidOperationException(MacNative.MissingLibraryMessage, ex));
        }

        return status == 0
            ? Task.CompletedTask
            : Task.FromException(
                new ExternalException($"NSPasteboard text write failed with status {status}."));
    }
}
