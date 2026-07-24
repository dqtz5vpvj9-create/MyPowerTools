using System.Buffers.Binary;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardImageService : IClipboardImageService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ClipboardImagePayload> ReadPngAsync(CancellationToken cancellationToken)
    {
        byte[]? nativePng = null;
        nint bitmapCopy = 0;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OpenClipboardWithRetry(cancellationToken);
            try
            {
                nativePng = TryReadNativeClipboardPng();
                if (nativePng is null)
                {
                    var bitmapHandle = NativeMethods.GetClipboardData(NativeMethods.CfBitmap);
                    if (bitmapHandle == 0)
                    {
                        throw new InvalidOperationException("No image is available in the clipboard.");
                    }

                    bitmapCopy = NativeMethods.CopyImage(
                        bitmapHandle,
                        NativeMethods.ImageBitmap,
                        0,
                        0,
                        NativeMethods.LrCreatedibsection);
                    if (bitmapCopy == 0)
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Could not duplicate the clipboard bitmap.");
                    }
                }
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (nativePng is not null)
        {
            var (width, height) = ReadPngDimensions(nativePng);
            return new ClipboardImagePayload(nativePng, width, height);
        }

        try
        {
            using var image = Image.FromHbitmap(bitmapCopy);
            using var output = new MemoryStream();
            image.Save(output, ImageFormat.Png);
            return new ClipboardImagePayload(output.ToArray(), image.Width, image.Height, UsedNativePng: false);
        }
        finally
        {
            if (bitmapCopy != 0)
            {
                NativeMethods.DeleteObject(bitmapCopy);
            }
        }
    }

    public async Task WriteTextAsync(string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OpenClipboardWithRetry(cancellationToken);
            nint memory = 0;
            try
            {
                if (!NativeMethods.EmptyClipboard())
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not clear the clipboard.");
                }

                var bytes = System.Text.Encoding.Unicode.GetBytes(value + '\0');
                memory = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, (nuint)bytes.Length);
                if (memory == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate clipboard memory.");
                }

                var destination = NativeMethods.GlobalLock(memory);
                if (destination == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not lock clipboard memory.");
                }

                try
                {
                    Marshal.Copy(bytes, 0, destination, bytes.Length);
                }
                finally
                {
                    NativeMethods.GlobalUnlock(memory);
                }

                if (NativeMethods.SetClipboardData(NativeMethods.CfUnicodeText, memory) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not set clipboard text.");
                }

                memory = 0;
            }
            finally
            {
                if (memory != 0)
                {
                    NativeMethods.GlobalFree(memory);
                }
                NativeMethods.CloseClipboard();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static byte[]? TryReadNativeClipboardPng()
    {
        foreach (var format in NativeMethods.ClipboardPngFormats)
        {
            if (format == 0 || !NativeMethods.IsClipboardFormatAvailable(format))
            {
                continue;
            }

            var handle = NativeMethods.GetClipboardData(format);
            var size = handle == 0 ? 0 : NativeMethods.GlobalSize(handle);
            if (size is 0 or > 268_435_456)
            {
                continue;
            }

            var source = NativeMethods.GlobalLock(handle);
            if (source == 0)
            {
                continue;
            }

            try
            {
                var bytes = new byte[(int)size];
                Marshal.Copy(source, bytes, 0, bytes.Length);
                if (HasPngSignature(bytes))
                {
                    return bytes;
                }
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }

        return null;
    }

    private static bool HasPngSignature(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 24 &&
        bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71 &&
        bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10 &&
        bytes.Slice(12, 4).SequenceEqual("IHDR"u8);

    private static (int Width, int Height) ReadPngDimensions(ReadOnlySpan<byte> bytes)
    {
        if (!HasPngSignature(bytes))
        {
            throw new InvalidDataException("The clipboard PNG payload is invalid.");
        }

        return (
            BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4)));
    }

    private static void OpenClipboardWithRetry(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.OpenClipboard(0))
            {
                return;
            }
            if (attempt < 8)
            {
                Thread.Sleep(1 << attempt);
            }
        }

        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the clipboard.");
    }

    private static class NativeMethods
    {
        public const uint CfBitmap = 2;
        public const uint CfUnicodeText = 13;
        public const uint GmemMoveable = 0x0002;
        public const uint ImageBitmap = 0;
        public const uint LrCreatedibsection = 0x00002000;

        public static readonly uint[] ClipboardPngFormats =
            [RegisterClipboardFormat("PNG"), RegisterClipboardFormat("image/png")];

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenClipboard(nint windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint GetClipboardData(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint CopyImage(nint handle, uint type, int desiredWidth, int desiredHeight, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint RegisterClipboardFormat(string format);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetClipboardData(uint format, nint memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint GlobalAlloc(uint flags, nuint bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint GlobalLock(nint memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalUnlock(nint memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint GlobalFree(nint memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nuint GlobalSize(nint memory);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(nint handle);
    }
}
