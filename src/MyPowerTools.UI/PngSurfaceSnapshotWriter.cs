using MyPowerTools.Packaging;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace MyPowerTools.UI;

internal static class PngSurfaceSnapshotWriter
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static PixelSnapshotResult Write(
        string outputPath,
        string fileName,
        UiSnapshotRequest request,
        string packageId,
        MptUiSurfaceManifest surface)
    {
        var (width, height) = ParseSize(request.Size);
        var palette = SnapshotPalette.Create(surface, request);
        var raster = new RgbaRaster(width, height);
        raster.Clear(palette.Background);

        DrawSurface(raster, palette, surface, request, packageId);

        var png = EncodePng(raster);
        File.WriteAllBytes(outputPath, png);

        var sha256 = Convert.ToHexString(SHA256.HashData(png));
        return new PixelSnapshotResult(
            fileName,
            width,
            height,
            sha256,
            raster.CountUniqueColors(),
            raster.CountPixelsDifferentFrom(palette.Background));
    }

    private static void DrawSurface(
        RgbaRaster raster,
        SnapshotPalette palette,
        MptUiSurfaceManifest surface,
        UiSnapshotRequest request,
        string packageId)
    {
        var margin = Clamp(raster.Width / 32, 18, 56);
        var gap = Clamp(raster.Width / 90, 8, 20);
        var panelX = margin;
        var panelY = margin;
        var panelW = raster.Width - (margin * 2);
        var panelH = raster.Height - (margin * 2);
        var headerH = Clamp(raster.Height / 8, 58, 116);
        var stateH = Clamp(raster.Height / 12, 46, 86);
        var densityScale = request.Density.Equals("compact", StringComparison.OrdinalIgnoreCase)
            ? 0.78
            : request.Density.Equals("comfortable", StringComparison.OrdinalIgnoreCase) ? 1.14 : 1.0;

        raster.FillRect(panelX, panelY, panelW, panelH, palette.Panel);
        raster.StrokeRect(panelX, panelY, panelW, panelH, Clamp(raster.Width / 360, 2, 5), palette.Border);
        raster.FillRect(panelX, panelY, panelW, Clamp(headerH / 12, 6, 12), palette.Accent);

        var headerPad = Clamp((int)(24 * densityScale), 16, 32);
        var titleW = Clamp(panelW / 3, 180, panelW - (headerPad * 2));
        var titleH = Clamp((int)(14 * densityScale), 10, 18);
        raster.FillRect(panelX + headerPad, panelY + headerPad, titleW, titleH, palette.PrimaryLine);
        raster.FillRect(panelX + headerPad, panelY + headerPad + titleH + 10, Math.Max(titleW / 2, 96), Math.Max(6, titleH / 2), palette.SecondaryLine);

        DrawSignatureBars(raster, palette, panelX + panelW - headerPad - 180, panelY + headerPad, 180, headerH - headerPad, surface.SurfaceId + packageId);

        var contentY = panelY + headerH + gap;
        var contentH = Math.Max(32, panelH - headerH - stateH - (gap * 3));
        var components = surface.Uses.Count == 0 ? ["MptEmptyState"] : surface.Uses;
        var columns = Clamp(panelW / 320, 1, 4);
        var rows = Math.Max(1, (int)Math.Ceiling(components.Count / (double)columns));
        var cardW = Math.Max(1, (panelW - (gap * (columns + 1))) / columns);
        var cardH = Math.Max(28, Math.Min(Clamp(raster.Height / 5, 72, 150), (contentH - (gap * (rows + 1))) / rows));

        for (var i = 0; i < components.Count; i++)
        {
            var column = i % columns;
            var row = i / columns;
            var x = panelX + gap + (column * (cardW + gap));
            var y = contentY + gap + (row * (cardH + gap));
            var componentColor = ComponentColor(components[i], palette, i);
            DrawComponentBlock(raster, palette, componentColor, x, y, cardW, cardH, i);
        }

        var stateY = panelY + panelH - stateH - gap;
        DrawStateBand(raster, palette, surface.States, panelX + gap, stateY, panelW - (gap * 2), stateH);

        var footerH = Clamp(raster.Height / 75, 6, 12);
        var footerSeed = $"{surface.Kind}|{surface.ModuleId}|{request.Theme}|{request.Size}";
        DrawFooterSignal(raster, palette, panelX + gap, panelY + panelH - footerH - 4, panelW - (gap * 2), footerH, footerSeed);
    }

    private static void DrawComponentBlock(RgbaRaster raster, SnapshotPalette palette, Rgba componentColor, int x, int y, int width, int height, int index)
    {
        raster.FillRect(x, y, width, height, palette.Surface);
        raster.StrokeRect(x, y, width, height, 2, palette.Border);
        raster.FillRect(x, y, Clamp(width / 42, 5, 12), height, componentColor);

        var pad = Clamp(width / 22, 10, 22);
        var lineH = Clamp(height / 14, 5, 12);
        raster.FillRect(x + pad, y + pad, Math.Max(18, width / 2), lineH, palette.PrimaryLine);
        raster.FillRect(x + pad, y + pad + (lineH * 2), Math.Max(14, width / 3), Math.Max(4, lineH - 2), palette.SecondaryLine);

        var metricCount = 2 + (index % 3);
        var metricW = Math.Max(10, (width - (pad * 2) - ((metricCount - 1) * 8)) / metricCount);
        var metricY = y + height - pad - Clamp(height / 5, 16, 34);
        for (var i = 0; i < metricCount; i++)
        {
            var metricX = x + pad + (i * (metricW + 8));
            raster.FillRect(metricX, metricY, metricW, Math.Max(10, height / 6), Blend(componentColor, palette.Panel, 0.58));
        }
    }

    private static void DrawStateBand(RgbaRaster raster, SnapshotPalette palette, IReadOnlyList<string> states, int x, int y, int width, int height)
    {
        raster.FillRect(x, y, width, height, palette.Surface);
        raster.StrokeRect(x, y, width, height, 2, palette.Border);

        var values = states.Count == 0 ? ["ready"] : states;
        var gap = Clamp(width / 90, 6, 14);
        var pillH = Clamp(height / 3, 14, 24);
        var pillY = y + ((height - pillH) / 2);
        var pillW = Math.Max(24, (width - (gap * (values.Count + 1))) / values.Count);

        for (var i = 0; i < values.Count; i++)
        {
            var pillX = x + gap + (i * (pillW + gap));
            raster.FillRect(pillX, pillY, pillW, pillH, StateColor(values[i], palette));
            raster.FillRect(pillX + 5, pillY + 5, Math.Max(4, pillW / 5), Math.Max(4, pillH - 10), palette.Panel);
        }
    }

    private static void DrawSignatureBars(RgbaRaster raster, SnapshotPalette palette, int x, int y, int width, int height, string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var barCount = 12;
        var gap = 4;
        var barW = Math.Max(3, (width - (gap * (barCount - 1))) / barCount);
        for (var i = 0; i < barCount; i++)
        {
            var barH = Clamp((hash[i] % Math.Max(1, height - 14)) + 10, 10, height);
            var barX = x + (i * (barW + gap));
            var barY = y + height - barH;
            raster.FillRect(barX, barY, barW, barH, i % 2 == 0 ? palette.AccentSoft : palette.SecondaryLine);
        }
    }

    private static void DrawFooterSignal(RgbaRaster raster, SnapshotPalette palette, int x, int y, int width, int height, string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var cursor = x;
        for (var i = 0; i < hash.Length && cursor < x + width; i++)
        {
            var segment = Clamp(hash[i] % 90, 16, 90);
            raster.FillRect(cursor, y, Math.Min(segment, x + width - cursor), height, i % 3 == 0 ? palette.Accent : palette.Border);
            cursor += segment + 5;
        }
    }

    private static Rgba ComponentColor(string value, SnapshotPalette palette, int index)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var color = new Rgba(
            (byte)(72 + (hash[(index + 0) % hash.Length] % 136)),
            (byte)(72 + (hash[(index + 7) % hash.Length] % 136)),
            (byte)(72 + (hash[(index + 13) % hash.Length] % 136)));
        return Blend(color, palette.Accent, 0.35);
    }

    private static Rgba StateColor(string state, SnapshotPalette palette)
    {
        return state.ToLowerInvariant() switch
        {
            "ready" => new Rgba(34, 197, 94),
            "loading" => new Rgba(59, 130, 246),
            "degraded" => new Rgba(245, 158, 11),
            "error" => new Rgba(239, 68, 68),
            "permission-required" => new Rgba(168, 85, 247),
            "disabled" => new Rgba(100, 116, 139),
            "empty" => palette.SecondaryLine,
            _ => palette.AccentSoft
        };
    }

    private static byte[] EncodePng(RgbaRaster raster)
    {
        var stride = (raster.Width * 4) + 1;
        var raw = new byte[stride * raster.Height];
        for (var y = 0; y < raster.Height; y++)
        {
            var rawOffset = y * stride;
            raw[rawOffset] = 0;
            Buffer.BlockCopy(raster.Buffer, y * raster.Width * 4, raw, rawOffset + 1, raster.Width * 4);
        }

        using var compressedStream = new MemoryStream();
        using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var pngStream = new MemoryStream();
        pngStream.Write(PngSignature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], raster.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), raster.Height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;

        WriteChunk(pngStream, "IHDR", ihdr);
        WriteChunk(pngStream, "IDAT", compressedStream.ToArray());
        WriteChunk(pngStream, "IEND", ReadOnlySpan<byte>.Empty);
        return pngStream.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = UpdateCrc(0xFFFFFFFF, typeBytes);
        crc = UpdateCrc(crc, data);
        crc ^= 0xFFFFFFFF;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) == 1 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static (int Width, int Height) ParseSize(string size)
    {
        var parts = size.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var width) && int.TryParse(parts[1], out var height))
        {
            return (Clamp(width, 320, 2560), Clamp(height, 240, 1600));
        }

        return (1366, 768);
    }

    private static int Clamp(int value, int min, int max)
    {
        return Math.Min(max, Math.Max(min, value));
    }

    private static Rgba Blend(Rgba foreground, Rgba background, double backgroundWeight)
    {
        var foregroundWeight = 1.0 - backgroundWeight;
        return new Rgba(
            (byte)Math.Clamp((foreground.R * foregroundWeight) + (background.R * backgroundWeight), 0, 255),
            (byte)Math.Clamp((foreground.G * foregroundWeight) + (background.G * backgroundWeight), 0, 255),
            (byte)Math.Clamp((foreground.B * foregroundWeight) + (background.B * backgroundWeight), 0, 255),
            255);
    }

    private sealed class RgbaRaster(int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte[] Buffer { get; } = new byte[width * height * 4];

        public void Clear(Rgba color)
        {
            FillRect(0, 0, Width, Height, color);
        }

        public void FillRect(int x, int y, int width, int height, Rgba color)
        {
            var left = Clamp(x, 0, Width);
            var top = Clamp(y, 0, Height);
            var right = Clamp(x + width, 0, Width);
            var bottom = Clamp(y + height, 0, Height);
            for (var yy = top; yy < bottom; yy++)
            {
                var row = (yy * Width + left) * 4;
                for (var xx = left; xx < right; xx++)
                {
                    Buffer[row++] = color.R;
                    Buffer[row++] = color.G;
                    Buffer[row++] = color.B;
                    Buffer[row++] = color.A;
                }
            }
        }

        public void StrokeRect(int x, int y, int width, int height, int thickness, Rgba color)
        {
            FillRect(x, y, width, thickness, color);
            FillRect(x, y + height - thickness, width, thickness, color);
            FillRect(x, y, thickness, height, color);
            FillRect(x + width - thickness, y, thickness, height, color);
        }

        public int CountUniqueColors()
        {
            var colors = new HashSet<int>();
            for (var i = 0; i < Buffer.Length; i += 4)
            {
                colors.Add((Buffer[i + 3] << 24) | (Buffer[i] << 16) | (Buffer[i + 1] << 8) | Buffer[i + 2]);
            }

            return colors.Count;
        }

        public int CountPixelsDifferentFrom(Rgba color)
        {
            var count = 0;
            for (var i = 0; i < Buffer.Length; i += 4)
            {
                if (Buffer[i] != color.R || Buffer[i + 1] != color.G || Buffer[i + 2] != color.B || Buffer[i + 3] != color.A)
                {
                    count++;
                }
            }

            return count;
        }
    }

    private sealed record SnapshotPalette(
        Rgba Background,
        Rgba Panel,
        Rgba Surface,
        Rgba Border,
        Rgba Accent,
        Rgba AccentSoft,
        Rgba PrimaryLine,
        Rgba SecondaryLine)
    {
        public static SnapshotPalette Create(MptUiSurfaceManifest surface, UiSnapshotRequest request)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{surface.SurfaceId}|{request.Theme}|{request.Density}"));
            var accent = new Rgba(
                (byte)(64 + (hash[0] % 128)),
                (byte)(64 + (hash[1] % 128)),
                (byte)(64 + (hash[2] % 128)));

            if (request.Theme.Equals("dark", StringComparison.OrdinalIgnoreCase))
            {
                var panel = new Rgba(31, 41, 55);
                return new SnapshotPalette(
                    new Rgba(17, 24, 39),
                    panel,
                    new Rgba(45, 55, 72),
                    new Rgba(100, 116, 139),
                    accent,
                    Blend(accent, panel, 0.58),
                    new Rgba(226, 232, 240),
                    new Rgba(148, 163, 184));
            }

            var lightPanel = new Rgba(255, 255, 255);
            return new SnapshotPalette(
                new Rgba(248, 250, 252),
                lightPanel,
                new Rgba(241, 245, 249),
                new Rgba(148, 163, 184),
                accent,
                Blend(accent, lightPanel, 0.68),
                new Rgba(51, 65, 85),
                new Rgba(100, 116, 139));
        }
    }

    private readonly record struct Rgba(byte R, byte G, byte B, byte A = 255);
}

public sealed record PixelSnapshotResult(
    string FileName,
    int Width,
    int Height,
    string Sha256,
    int UniqueColorCount,
    int NonBackgroundPixels);
