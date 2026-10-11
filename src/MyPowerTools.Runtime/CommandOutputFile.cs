using System.IO.Compression;
using System.Text;
using System.Runtime.InteropServices;

namespace MyPowerTools.Runtime;

/// <summary>Stores large completed outputs without retaining their UTF-16 strings.</summary>
internal static class CommandOutputFile
{
    public static string SessionDirectory(string root)
    {
        // Prune abandoned sessions once at startup; active processes keep their outputs.
        try
        {
            if (Directory.Exists(root))
            {
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    var parts = Path.GetFileName(directory).Split('-');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out var owner) || owner == Environment.ProcessId ||
                        !Guid.TryParseExact(parts[1], "N", out _) || Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddDays(-1)) continue;
                    try { using var process = System.Diagnostics.Process.GetProcessById(owner); }
                    catch (ArgumentException) { DeleteDirectory(directory); }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Path.Combine(root, $"{Environment.ProcessId}-{Guid.NewGuid():N}");
    }

    public static string Write(string directory, string output)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.gz");
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var compressed = new GZipStream(file, CompressionLevel.Fastest);
            // Raw UTF-16 preserves even unmatched surrogates without transcoding buffers.
            using var writer = new BinaryWriter(compressed, Encoding.UTF8, leaveOpen: true);
            writer.Write(output.Length);
            compressed.Write(MemoryMarshal.AsBytes(output.AsSpan()));
            return path;
        }
        catch
        {
            Delete(path);
            throw;
        }
    }

    public static string Read(string path)
    {
        using var file = File.OpenRead(path);
        using var compressed = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(compressed, Encoding.UTF8, leaveOpen: true);
        var length = reader.ReadInt32();
        if (length < 0 || length > int.MaxValue / sizeof(char)) throw new InvalidDataException("Invalid command output length.");
        var characters = new char[length];
        compressed.ReadExactly(MemoryMarshal.AsBytes(characters.AsSpan()));
        return new string(characters);
    }

    public static void Delete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void DeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
