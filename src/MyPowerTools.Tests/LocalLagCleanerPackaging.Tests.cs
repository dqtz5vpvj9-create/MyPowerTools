using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MyPowerTools.Cli;

namespace MyPowerTools.Tests;

/// <summary>
/// Guards the packaged layout of the SDK tools that declare their runtime and Surface paths in
/// tool.json. A build with a non-Release configuration used to leave those declared directories
/// empty (raw bin/Debug is excluded by .mptignore), so the package shipped without a runtime.
/// The check mirrors the declared layout, packs it with the real ToolScaffolder and verifies the
/// package actually resolves every manifest reference, including dependency manifests.
/// </summary>
public sealed class LocalLagCleanerPackagingTests
{
    [Fact]
    public void Declared_runtime_surface_and_dependencies_survive_packing()
    {
        var repositoryRoot = RootPath();
        var sdkToolRoot = Path.Combine(repositoryRoot, "tools", "local-lag-cleaner", "sdk-tool");
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(sdkToolRoot, "tool.json")))!.AsObject();
        var runtimeArtifact = manifest["runtime"]!["command"]!.GetValue<string>().Replace('\\', '/');
        var surfaceArtifact = manifest["routes"]!.AsArray()[0]!["surface"]!["assembly"]!.GetValue<string>().Replace('\\', '/');

        var scratch = Path.Combine(Path.GetTempPath(), "mpt-local-lag-pack", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            foreach (var metadata in new[] { "tool.json", ".mptignore", "settings.json", "settings.schema.json" })
            {
                File.Copy(Path.Combine(sdkToolRoot, metadata), Path.Combine(scratch, metadata));
            }

            CopyDirectory(Path.Combine(sdkToolRoot, "ui"), Path.Combine(scratch, "ui"));

            // The build script compiles the selected configuration with --output into the declared
            // layout, so this is exactly what the packer sees for a Debug developer build.
            WriteArtifact(scratch, runtimeArtifact, runtimeConfig: true);
            WriteArtifact(scratch, surfaceArtifact, runtimeConfig: false);

            // Raw Debug output stays excluded: the fix routes Debug builds into the declared layout
            // instead of teaching the shipped .mptignore about Debug.
            var decoy = Path.Combine(scratch, "src", "LocalLagCleaner.Runtime", "bin", "Debug", "net10.0", "LocalLagCleaner.Runtime.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(decoy)!);
            File.WriteAllText(decoy, "raw-debug-output-must-not-ship");

            var packagePath = Path.Combine(scratch, "local-lag-cleaner.mptpkg");
            Assert.Equal(0, ToolScaffolder.Pack(scratch, packagePath, Path.Combine(repositoryRoot, "schemas")));

            using var package = ZipFile.OpenRead(packagePath);
            var entries = package.Entries.ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
            var runtimeDirectory = Path.GetDirectoryName(runtimeArtifact)!.Replace('\\', '/');
            var runtimeStem = Path.GetFileNameWithoutExtension(runtimeArtifact);
            var surfaceDirectory = Path.GetDirectoryName(surfaceArtifact)!.Replace('\\', '/');
            var surfaceStem = Path.GetFileNameWithoutExtension(surfaceArtifact);

            Assert.Contains(runtimeArtifact, entries.Keys);
            Assert.Contains(surfaceArtifact, entries.Keys);
            Assert.Contains($"{runtimeDirectory}/{runtimeStem}.deps.json", entries.Keys);
            Assert.Contains($"{runtimeDirectory}/{runtimeStem}.runtimeconfig.json", entries.Keys);
            Assert.Contains($"{runtimeDirectory}/LocalLagCleaner.Core.dll", entries.Keys);
            Assert.Contains($"{surfaceDirectory}/{surfaceStem}.deps.json", entries.Keys);
            Assert.Contains($"{surfaceDirectory}/LocalLagCleaner.Core.dll", entries.Keys);
            Assert.Contains("ui/detail-page.json", entries.Keys);
            Assert.DoesNotContain(entries.Keys, name => name.Contains("/bin/Debug/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(entries.Keys, name => name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));

            // The generated source manifest must describe the bytes that were actually packed.
            var sourceManifest = JsonNode.Parse(ReadText(entries["source-manifest.json"]))!.AsObject();
            var hashes = sourceManifest["files"]!.AsArray().ToDictionary(
                file => file!["path"]!.GetValue<string>(),
                file => file!["sha256"]!.GetValue<string>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var reference in new[] { runtimeArtifact, surfaceArtifact, "tool.json" })
            {
                var actual = Convert.ToHexString(SHA256.HashData(ReadBytes(entries[reference]))).ToLowerInvariant();
                Assert.Equal(actual, hashes[reference]);
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void WriteArtifact(string root, string relativePath, bool runtimeConfig)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(path);
        File.WriteAllText(path, $"artifact:{relativePath}");
        File.WriteAllText(Path.Combine(directory, stem + ".deps.json"), "{}");
        if (runtimeConfig)
        {
            File.WriteAllText(Path.Combine(directory, stem + ".runtimeconfig.json"), "{}");
        }

        File.WriteAllText(Path.Combine(directory, "LocalLagCleaner.Core.dll"), "core");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string ReadText(ZipArchiveEntry entry) => Encoding.UTF8.GetString(ReadBytes(entry));

    private static string RootPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
