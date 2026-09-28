using MyPowerTools.Android.Files;

namespace AndroidFileOpen.Tests;

public sealed class SharedFileViewPlanTests
{
    [Theory]
    [InlineData("report.pdf", "application/pdf")]
    [InlineData("报告 v1.pdf", "application/pdf")]
    [InlineData("blob.qqq", SharedFileMime.BinaryMimeType)]
    [InlineData("noextension", SharedFileMime.BinaryMimeType)]
    public void TheInferredTypeIsTriedFirstAndTheWildcardIsTheOnlyRetry(string fileName, string expectedPrimary)
    {
        var plan = SharedFileViewPlan.Create(fileName);

        Assert.Equal(expectedPrimary, plan.PrimaryMimeType);
        Assert.Equal(SharedFileMime.WildcardMimeType, plan.FallbackMimeType);
        Assert.Equal(new[] { expectedPrimary, SharedFileMime.WildcardMimeType }, plan.MimeAttempts);
        Assert.True(plan.MimeAttempts.Count <= SharedFileViewPlan.MaxAttempts);
    }

    [Fact]
    public void TheFirstAttemptIsNeverTheWildcard()
    {
        string[] names = ["a.pdf", "b.PNG", "c.qqq", "d", "", "e.tar.gz", "报告.docx", ".hidden"];

        foreach (var name in names)
        {
            var plan = SharedFileViewPlan.Create(name);
            Assert.NotEqual(SharedFileMime.WildcardMimeType, plan.PrimaryMimeType);
            Assert.Equal(2, plan.MimeAttempts.Count);
        }
    }

    [Fact]
    public void AWildcardFromThePlatformMapCollapsesToOneAttempt()
    {
        var plan = SharedFileViewPlan.Create("blob.qqq", _ => SharedFileMime.WildcardMimeType);

        Assert.Equal(SharedFileMime.WildcardMimeType, plan.PrimaryMimeType);
        Assert.Equal(new[] { SharedFileMime.WildcardMimeType }, plan.MimeAttempts);
        Assert.True(plan.MimeAttempts.Count <= SharedFileViewPlan.MaxAttempts);
    }

    [Fact]
    public void ThePlanReportsWhereTheTypeCameFrom()
    {
        Assert.Equal(SharedFileMimeSource.KnownExtension, SharedFileViewPlan.Create("a.pdf").Source);
        Assert.Equal(SharedFileMimeSource.SystemMap, SharedFileViewPlan.Create("a.qqq", _ => "application/x-qqq").Source);
        Assert.Equal(SharedFileMimeSource.Unknown, SharedFileViewPlan.Create("a.qqq").Source);
    }
}
