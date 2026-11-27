using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
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
            var (pngBytes, width, height) = EncodeHBitmapAsPng(bitmapCopy);
            return new ClipboardImagePayload(pngBytes, width, height, UsedNativePng: false);
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

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CreateStreamOnHGlobal(IntPtr hGlobal, [MarshalAs(UnmanagedType.Bool)] bool fDeleteOnRelease, out IStream stream);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipCreateBitmapFromHBITMAP(nint hbm, nint hpal, out nint bitmap);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipDisposeImage(nint image);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipGetImageWidth(nint image, out uint width);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipGetImageHeight(nint image, out uint height);

        [DllImport("gdiplus.dll", ExactSpelling = true)]
        public static extern int GdipSaveImageToStream(nint image, IStream stream, ref Guid encoderClsid, nint encoderParams);
    }

    private static readonly Guid PngEncoderClsid = new("557CF406-1A04-11D3-9A73-0000F81EF32E");

    private static (byte[] PngBytes, int Width, int Height) EncodeHBitmapAsPng(nint hBitmap)
    {
        CodexQuotaIconRenderer.EnsureGdiPlusInitialized();
        nint gdipBitmap = 0;
        IStream? stream = null;
        try
        {
            int status = NativeMethods.GdipCreateBitmapFromHBITMAP(hBitmap, 0, out gdipBitmap);
            if (status != 0 || gdipBitmap == 0)
            {
                throw new InvalidOperationException($"GdipCreateBitmapFromHBITMAP failed with status {status}.");
            }

            status = NativeMethods.GdipGetImageWidth(gdipBitmap, out uint width);
            if (status != 0)
            {
                throw new InvalidOperationException($"GdipGetImageWidth failed with status {status}.");
            }

            status = NativeMethods.GdipGetImageHeight(gdipBitmap, out uint height);
            if (status != 0)
            {
                throw new InvalidOperationException($"GdipGetImageHeight failed with status {status}.");
            }

            int hr = NativeMethods.CreateStreamOnHGlobal(IntPtr.Zero, fDeleteOnRelease: true, out stream);
            if (hr < 0 || stream is null)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            Guid encoder = PngEncoderClsid;
            status = NativeMethods.GdipSaveImageToStream(gdipBitmap, stream!, ref encoder, 0);
            if (status != 0)
            {
                throw new InvalidOperationException($"GdipSaveImageToStream(PNG) failed with status {status}.");
            }

            stream!.Stat(out STATSTG stat, 1 /* STATFLAG_NONAME */);
            int length = checked((int)stat.cbSize);
            byte[] buffer = new byte[length];
            stream.Seek(0, 0 /* STREAM_SEEK_SET */, IntPtr.Zero);
            stream.Read(buffer, length, IntPtr.Zero);
            return (buffer, (int)width, (int)height);
        }
        finally
        {
            if (stream is not null && Marshal.IsComObject(stream))
            {
                Marshal.ReleaseComObject(stream);
            }

            if (gdipBitmap != 0)
            {
                NativeMethods.GdipDisposeImage(gdipBitmap);
            }
        }
    }
}
