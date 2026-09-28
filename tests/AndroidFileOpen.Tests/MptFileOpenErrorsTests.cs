using MyPowerTools.Android.Files;

namespace AndroidFileOpen.Tests;

public sealed class MptFileOpenErrorsTests
{
    [Theory]
    [InlineData(SharedFileAccessVerdict.EmptyPath, MptFileOpenFailure.EmptyPath)]
    [InlineData(SharedFileAccessVerdict.MalformedPath, MptFileOpenFailure.MalformedPath)]
    [InlineData(SharedFileAccessVerdict.UnsupportedScheme, MptFileOpenFailure.UnsupportedScheme)]
    [InlineData(SharedFileAccessVerdict.OutsideAllowedRoots, MptFileOpenFailure.OutsideAllowedRoots)]
    [InlineData(SharedFileAccessVerdict.Missing, MptFileOpenFailure.Missing)]
    [InlineData(SharedFileAccessVerdict.NotAFile, MptFileOpenFailure.NotAFile)]
    [InlineData(SharedFileAccessVerdict.Unreadable, MptFileOpenFailure.Unreadable)]
    public void EveryRejectedVerdictBecomesAConcreteFailure(SharedFileAccessVerdict verdict, MptFileOpenFailure expected)
    {
        var error = MptFileOpenErrors.FromVerdict(verdict);

        Assert.Equal(expected, error.Failure);
        Assert.StartsWith("打开文件失败：", error.Message, StringComparison.Ordinal);
        Assert.Empty(error.AllowedRoots);
    }

    [Fact]
    public void AllowedIsNotAFailureAndIsATypedCallerBug()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MptFileOpenErrors.FromVerdict(SharedFileAccessVerdict.Allowed));
    }

    [Fact]
    public void OnlyTheOutOfBoundsFailureCarriesTheRootsForDiagnostics()
    {
        string[] roots = ["/data/user/0/com.mypowertools.android/files/MyPowerTools/state/modules/file-transfer/data/incoming"];

        var outside = MptFileOpenErrors.FromVerdict(SharedFileAccessVerdict.OutsideAllowedRoots, roots);
        var missing = MptFileOpenErrors.FromVerdict(SharedFileAccessVerdict.Missing, roots);

        Assert.Equal(roots, outside.AllowedRoots);
        Assert.Empty(missing.AllowedRoots);
        // The rejected path itself is never echoed into the message.
        Assert.DoesNotContain("incoming", outside.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNoViewerMessageNamesTheTypeButNotTheFileName()
    {
        var error = MptFileOpenErrors.NoViewer("/data/user/0/pkg/files/收件/机密合同 2026.pdf");

        Assert.Equal(MptFileOpenFailure.NoViewer, error.Failure);
        Assert.Contains(".pdf", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("机密合同", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/data/", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureWithoutAnExtensionStillReadsAsASentence()
    {
        var error = MptFileOpenErrors.NoViewer("noextension");

        Assert.Contains("此文件", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderAndLaunchFailuresKeepTheInnerException()
    {
        var inner = new InvalidOperationException("inner");
        var provider = MptFileOpenErrors.ProviderMisconfigured("（authority 不匹配）", inner);
        var launch = MptFileOpenErrors.LaunchFailed("（系统拒绝）", inner);

        Assert.Equal(MptFileOpenFailure.ProviderMisconfigured, provider.Failure);
        Assert.Equal(MptFileOpenFailure.LaunchFailed, launch.Failure);
        Assert.Same(inner, provider.InnerException);
        Assert.Same(inner, launch.InnerException);
        Assert.Contains("authority", provider.Message, StringComparison.Ordinal);
    }
}
