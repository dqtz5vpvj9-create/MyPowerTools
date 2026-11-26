using MyPowerTools.Android.Files;

namespace AndroidFileOpen.Tests;

public sealed class SharedFileAccessPolicyTests
{
    [Fact]
    public void AFileInARootWithChineseAndSpacesIsAllowed()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.CreateDirectory("收件 目录");
        var file = workspace.CreateFile("收件 目录/报告 v1 (最终).pdf");

        var policy = new SharedFileAccessPolicy([root]);

        Assert.Equal(SharedFileAccessVerdict.Allowed, policy.Evaluate(file, out var normalized));
        Assert.Equal(Path.GetFullPath(file), normalized);
    }

    [Fact]
    public void AFileUriInputIsDecodedAndAllowed()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.CreateDirectory("shares");
        var file = workspace.CreateFile("shares/报告 空格.txt");

        var policy = new SharedFileAccessPolicy([root]);
        var uri = new Uri(file).AbsoluteUri;
        Assert.Contains("%20", uri, StringComparison.Ordinal);

        Assert.Equal(SharedFileAccessVerdict.Allowed, policy.Evaluate(uri, out var normalized));
        Assert.Equal(Path.GetFullPath(file), normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInputIsRejected(string? input)
    {
        using var workspace = new TempWorkspace();
        var policy = new SharedFileAccessPolicy([workspace.Root]);

        Assert.Equal(SharedFileAccessVerdict.EmptyPath, policy.Evaluate(input, out var normalized));
        Assert.Equal("", normalized);
    }

    [Fact]
    public void RelativePathsAreRejected()
    {
        using var workspace = new TempWorkspace();
        workspace.CreateFile("shares/a.txt");
        var policy = new SharedFileAccessPolicy([Path.Combine(workspace.Root, "shares")]);

        Assert.Equal(SharedFileAccessVerdict.MalformedPath, policy.Evaluate("shares/a.txt", out _));
        Assert.Equal(SharedFileAccessVerdict.MalformedPath, policy.Evaluate("./shares/a.txt", out _));
        Assert.Equal(SharedFileAccessVerdict.MalformedPath, policy.Evaluate("file:shares/a.txt", out _));
    }

    [Theory]
    [InlineData("content://media/external/file/42")]
    [InlineData("https://example.com/报告.pdf")]
    [InlineData("mpt://assistant/item")]
    public void OtherUriSchemesAreRejectedInsteadOfGuessedAt(string input)
    {
        using var workspace = new TempWorkspace();
        var policy = new SharedFileAccessPolicy([workspace.Root]);

        Assert.Equal(SharedFileAccessVerdict.UnsupportedScheme, policy.Evaluate(input, out _));
    }

    [Fact]
    public void AMissingFileIsReportedAsMissing()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.CreateDirectory("shares");
        var policy = new SharedFileAccessPolicy([root]);

        Assert.Equal(SharedFileAccessVerdict.Missing, policy.Evaluate(Path.Combine(root, "nope.pdf"), out _));
    }

    [Fact]
    public void ADirectoryIsNotAFile()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.CreateDirectory("shares");
        var directory = workspace.CreateDirectory("shares/nested");
        var policy = new SharedFileAccessPolicy([root]);

        Assert.Equal(SharedFileAccessVerdict.NotAFile, policy.Evaluate(directory, out _));
    }

    [Fact]
    public void APathOutsideEveryRootIsRejectedAndReportsTheRoots()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.CreateDirectory("shares");
        var outside = workspace.CreateFile("outside.pdf");
        var policy = new SharedFileAccessPolicy([root]);

        var verdict = policy.Evaluate(outside, out var normalized);

        Assert.Equal(SharedFileAccessVerdict.OutsideAllowedRoots, verdict);
        Assert.Equal("", normalized);
        Assert.Equal(new[] { Path.GetFullPath(root) }, policy.AllowedRoots);
    }

    [Fact]
    public void TraversalThatEscapesTheRootIsRejected()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.CreateDirectory("shares");
        var outside = workspace.CreateFile("outside.pdf");
        var policy = new SharedFileAccessPolicy([root]);
        var candidate = Path.Combine(root, "..", Path.GetFileName(outside));

        Assert.Equal(SharedFileAccessVerdict.OutsideAllowedRoots, policy.Evaluate(candidate, out _));
    }

    [Fact]
    public void ASiblingWithTheRootAsNamePrefixIsNotInside()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.CreateDirectory("shares");
        var sibling = workspace.CreateFile("shares-extra/a.pdf");
        var policy = new SharedFileAccessPolicy([root]);

        // /x/shares-extra is not inside /x/shares even though the string starts with it.
        Assert.Equal(SharedFileAccessVerdict.OutsideAllowedRoots, policy.Evaluate(sibling, out _));
        Assert.False(policy.Contains(Path.GetFullPath(sibling)));
        Assert.False(policy.Contains(Path.GetFullPath(root) + "-extra"));
        Assert.True(policy.Contains(Path.GetFullPath(root)));
        Assert.True(policy.Contains(Path.Combine(Path.GetFullPath(root), "deep/file.pdf")));
    }

    [Fact]
    public void ASymbolicLinkThatLeavesTheRootIsRejected()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.CreateDirectory("shares");
        var outside = workspace.CreateFile("outside.pdf");
        var policy = new SharedFileAccessPolicy([root]);
        var link = Path.Combine(root, "escape.pdf");
        if (!TryCreateSymbolicLink(link, outside))
        {
            return;
        }

        Assert.Equal(SharedFileAccessVerdict.OutsideAllowedRoots, policy.Evaluate(link, out _));
    }

    [Fact]
    public void ASymbolicLinkInsideTheRootResolvesToItsTarget()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.CreateDirectory("shares");
        var target = workspace.CreateFile("shares/real.pdf");
        var policy = new SharedFileAccessPolicy([root]);
        var link = Path.Combine(root, "alias.pdf");
        if (!TryCreateSymbolicLink(link, target))
        {
            return;
        }

        Assert.Equal(SharedFileAccessVerdict.Allowed, policy.Evaluate(link, out var normalized));
        Assert.Equal(Path.GetFullPath(target), normalized);
    }

    [Fact]
    public void ABrokenSymbolicLinkIsReportedAsMissingInsteadOfMalformed()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.CreateDirectory("shares");
        var policy = new SharedFileAccessPolicy([root]);
        var link = Path.Combine(root, "gone.pdf");
        if (!TryCreateSymbolicLink(link, Path.Combine(root, "never-existed.pdf")))
        {
            return;
        }

        Assert.Equal(SharedFileAccessVerdict.Missing, policy.Evaluate(link, out _));
    }

    [Fact]
    public void ARootWrittenWithATrailingSlashOrDotSegmentStillMatches()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.CreateDirectory("shares");
        var file = workspace.CreateFile("shares/a.pdf");
        var policy = new SharedFileAccessPolicy([root + Path.DirectorySeparatorChar, Path.Combine(root, ".")]);

        Assert.Equal(SharedFileAccessVerdict.Allowed, policy.Evaluate(file, out _));
        Assert.Single(policy.AllowedRoots);
    }

    [Fact]
    public void WithNoUsableRootNothingIsAllowed()
    {
        using var workspace = new TempWorkspace();
        var file = workspace.CreateFile("shares/a.pdf");
        var policy = new SharedFileAccessPolicy(["", "   ", "relative/root"]);

        Assert.Empty(policy.AllowedRoots);
        Assert.Equal(SharedFileAccessVerdict.OutsideAllowedRoots, policy.Evaluate(file, out _));
    }

    [Fact]
    public void AnUnreadableFileIsReportedBeforeTheViewerIsAsked()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        using var workspace = new TempWorkspace();
        var root = workspace.CreateDirectory("shares");
        var file = workspace.CreateFile("shares/locked.pdf");
        File.SetUnixFileMode(file, UnixFileMode.None);
        try
        {
            var policy = new SharedFileAccessPolicy([root]);

            Assert.Equal(SharedFileAccessVerdict.Unreadable, policy.Evaluate(file, out _));
        }
        finally
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void NestedRootsBothStayOpen()
    {
        using var workspace = new TempWorkspace();
        var outer = workspace.CreateDirectory("state");
        var inner = workspace.CreateDirectory("state/modules/file-transfer/data/incoming");
        var file = workspace.CreateFile("state/modules/file-transfer/data/incoming/a.pdf");
        var policy = new SharedFileAccessPolicy([outer, inner]);

        Assert.Equal(SharedFileAccessVerdict.Allowed, policy.Evaluate(file, out _));
        Assert.Equal(2, policy.AllowedRoots.Count);
    }

    private static bool TryCreateSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A filesystem without symbolic-link support cannot exercise this case.
            return false;
        }
    }
}

/// <summary>A disposable directory tree under the platform temp directory.</summary>
internal sealed class TempWorkspace : IDisposable
{
    internal TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "mpt-android-file-open", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    internal string Root { get; }

    internal string CreateDirectory(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    internal string CreateFile(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content is never read by the tests");
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory must not fail a test run.
        }
    }
}
