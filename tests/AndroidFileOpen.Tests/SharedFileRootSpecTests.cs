using MyPowerTools.Android.Files;

namespace AndroidFileOpen.Tests;

public sealed class SharedFileRootSpecTests
{
    [Fact]
    public void TheDeclaredRootsAreExactlyThePhoneReceiveAndStagingTrees()
    {
        Assert.Equal(
            new[]
            {
                "MyPowerTools/state/modules/file-transfer/data/assistant/payload",
                "MyPowerTools/state/modules/file-transfer/data/assistant/inbox",
                "MyPowerTools/state/modules/file-transfer/data/incoming",
                "MyPowerTools/state/modules/file-transfer/data/outbox",
                "shares",
            },
            SharedFileRootSpec.All.Select(root => root.RelativePath));
    }

    [Fact]
    public void RootNamesAndDirectoriesAreUnique()
    {
        Assert.Equal(SharedFileRootSpec.All.Count, SharedFileRootSpec.All.Select(root => root.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            SharedFileRootSpec.All.Count,
            SharedFileRootSpec.All.Select(root => (root.Kind, root.RelativePath)).Distinct().Count());
    }

    [Fact]
    public void EveryRootDeclaresARelativeDirectory()
    {
        foreach (var root in SharedFileRootSpec.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(root.Name));
            Assert.DoesNotContain('/', root.Name);
            Assert.DoesNotContain('\\', root.Name);
            Assert.False(Path.IsPathRooted(root.RelativePath));
            Assert.DoesNotContain("..", root.RelativePath, StringComparison.Ordinal);
            Assert.DoesNotContain("./", root.RelativePath, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", root.RelativePath, StringComparison.Ordinal);
            Assert.DoesNotContain("//", root.RelativePath, StringComparison.Ordinal);
            Assert.DoesNotContain("/./", root.RelativePath, StringComparison.Ordinal);
            Assert.Equal(root.RelativePath.Trim('/'), root.RelativePath);
            Assert.EndsWith("/", root.XmlPath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FilesRootsLiveBelowTheRuntimeFolderAndTheShareRootInTheCache()
    {
        foreach (var root in SharedFileRootSpec.All)
        {
            if (root.Kind == SharedFileRootKind.Files)
            {
                Assert.StartsWith(SharedFileRootSpec.RuntimeFolderName + "/", root.RelativePath, StringComparison.Ordinal);
                Assert.Equal("files-path", root.XmlElementName);
            }
            else
            {
                Assert.Equal("cache-path", root.XmlElementName);
                Assert.Equal("shares", root.RelativePath);
            }
        }
    }

    [Fact]
    public void NoRootOpensSettingsLogsOrTheAssistantStateFile()
    {
        // These are the app-private locations that carry secrets or message metadata. A root that
        // contains them would let a viewer read state the user never chose to open.
        string[] sensitive =
        [
            "MyPowerTools/settings",
            "MyPowerTools/logs",
            "MyPowerTools/packages",
            "MyPowerTools/state/modules/file-transfer/data/assistant",
            "MyPowerTools/state/modules/file-transfer/data/preferences.json",
            "MyPowerTools/state/modules/file-transfer/data/relay-account.json",
        ];

        foreach (var root in SharedFileRootSpec.All)
        {
            foreach (var location in sensitive)
            {
                Assert.False(
                    Covers(root.RelativePath, location),
                    $"{root.Name} ({root.RelativePath}) would expose {location}");
            }
        }
    }

    [Fact]
    public void ResolveAllCombinesEachRootWithItsOwnBaseDirectory()
    {
        var files = Path.Combine(Path.GetTempPath(), "mpt-files");
        var cache = Path.Combine(Path.GetTempPath(), "mpt-cache");

        var resolved = SharedFileRootSpec.ResolveAll(files, cache);

        Assert.Equal(SharedFileRootSpec.All.Count, resolved.Count);
        for (var index = 0; index < resolved.Count; index++)
        {
            var root = SharedFileRootSpec.All[index];
            var expected = Path.GetFullPath(Path.Combine(
                root.Kind == SharedFileRootKind.Files ? files : cache,
                root.RelativePath));
            Assert.Equal(expected, resolved[index]);
        }
    }

    [Fact]
    public void ResolveUnderRejectsAnEmptyBaseDirectory()
    {
        Assert.Throws<ArgumentException>(() => SharedFileRootSpec.OutgoingAttachments.ResolveUnder("  "));
    }

    [Fact]
    public void TheRuntimeLayoutMustBeExactlyTheDeclaredFolderName()
    {
        var files = "/data/user/0/com.mypowertools.android/files";

        Assert.Equal(
            SharedFileLayoutVerdict.Aligned,
            SharedFileRootSpec.VerifyRuntimeLayout(files, files + "/MyPowerTools", out var aligned));
        Assert.Equal("MyPowerTools", aligned);

        Assert.Equal(
            SharedFileLayoutVerdict.RuntimeFolderRenamed,
            SharedFileRootSpec.VerifyRuntimeLayout(files, files + "/.local/share/MyPowerTools", out var renamed));
        Assert.Equal(".local/share/MyPowerTools", renamed);

        Assert.Equal(
            SharedFileLayoutVerdict.RuntimeFolderRenamed,
            SharedFileRootSpec.VerifyRuntimeLayout(files, files, out _));

        Assert.Equal(
            SharedFileLayoutVerdict.RuntimeRootOutsideFilesDirectory,
            SharedFileRootSpec.VerifyRuntimeLayout(files, "/data/user/0/com.mypowertools.android/cache/MyPowerTools", out _));

        Assert.Equal(
            SharedFileLayoutVerdict.DirectoryUnavailable,
            SharedFileRootSpec.VerifyRuntimeLayout(null, files + "/MyPowerTools", out _));
        Assert.Equal(
            SharedFileLayoutVerdict.DirectoryUnavailable,
            SharedFileRootSpec.VerifyRuntimeLayout(files, "  ", out _));
    }

    [Fact]
    public void TheRuntimeLayoutCheckToleratesATrailingSeparator()
    {
        var files = "/data/user/0/com.mypowertools.android/files";

        Assert.Equal(
            SharedFileLayoutVerdict.Aligned,
            SharedFileRootSpec.VerifyRuntimeLayout(files + "/", files + "/MyPowerTools/", out var relative));
        Assert.Equal("MyPowerTools", relative);
    }

    private static bool Covers(string rootRelativePath, string location) =>
        string.Equals(rootRelativePath, location, StringComparison.Ordinal) ||
        location.StartsWith(rootRelativePath + "/", StringComparison.Ordinal);
}
