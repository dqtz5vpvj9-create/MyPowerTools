using MyPowerTools.Android.Files;

namespace AndroidFileOpen.Tests;

public sealed class SharedFileMimeTests
{
    [Theory]
    [InlineData("report.pdf", "application/pdf")]
    [InlineData("报告 v1.pdf", "application/pdf")]
    [InlineData("会议记录 2026.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("photo.PNG", "image/png")]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("voice.m4a", "audio/mp4")]
    [InlineData("notes.md", "text/markdown")]
    [InlineData("settings.yaml", "text/plain")]
    [InlineData("data.json", "application/json")]
    [InlineData("bundle.tar.gz", "application/gzip")]
    [InlineData("install.apk", "application/vnd.android.package-archive")]
    [InlineData("book.epub", "application/epub+zip")]
    public void KnownExtensionsResolveFromTheTable(string fileName, string expected)
    {
        var result = SharedFileMime.Infer(fileName);

        Assert.Equal(expected, result.MimeType);
        Assert.Equal(SharedFileMimeSource.KnownExtension, result.Source);
        Assert.False(result.IsUnknown);
    }

    [Theory]
    [InlineData("blob.qqq")]
    [InlineData("noextension")]
    [InlineData(".hidden")]
    [InlineData("trailing.")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("name.extensionwaytoolong")]
    public void UnrecognisedNamesFallBackToTheBinaryType(string fileName)
    {
        var result = SharedFileMime.Infer(fileName);

        Assert.Equal(SharedFileMime.BinaryMimeType, result.MimeType);
        Assert.Equal(SharedFileMimeSource.Unknown, result.Source);
        Assert.True(result.IsUnknown);
    }

    [Fact]
    public void PlatformMapIsOnlyConsultedWhenTheTableHasNoAnswer()
    {
        var calls = 0;
        string? Lookup(string extension)
        {
            calls++;
            return "application/x-" + extension;
        }

        var known = SharedFileMime.Infer("report.pdf", Lookup);
        Assert.Equal("application/pdf", known.MimeType);
        Assert.Equal(0, calls);

        var unknown = SharedFileMime.Infer("blob.qqq", Lookup);
        Assert.Equal("application/x-qqq", unknown.MimeType);
        Assert.Equal(SharedFileMimeSource.SystemMap, unknown.Source);
        Assert.Equal(1, calls);

        // An extension with no letters or digits is never handed to the platform map.
        var malformed = SharedFileMime.Infer("weird.$$$", Lookup);
        Assert.Equal(SharedFileMime.BinaryMimeType, malformed.MimeType);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void APlatformMapValueIsNormalisedBeforeItIsUsed()
    {
        var result = SharedFileMime.Infer("blob.qqq", _ => "  Application/X-Qqq ; charset=binary ");

        Assert.Equal("application/x-qqq", result.MimeType);
        Assert.Equal(SharedFileMimeSource.SystemMap, result.Source);
    }

    [Fact]
    public void AFailingOrUselessPlatformMapDoesNotBreakTheOpen()
    {
        Assert.Equal(SharedFileMime.BinaryMimeType, SharedFileMime.Infer("blob.qqq", _ => throw new InvalidOperationException("boom")).MimeType);
        Assert.Equal(SharedFileMime.BinaryMimeType, SharedFileMime.Infer("blob.qqq", _ => "   ").MimeType);
        Assert.Equal(SharedFileMime.BinaryMimeType, SharedFileMime.Infer("blob.qqq", _ => "not-a-mime").MimeType);
        Assert.Equal(SharedFileMime.BinaryMimeType, SharedFileMime.Infer("blob.qqq", _ => null).MimeType);
    }

    [Theory]
    [InlineData("/data/user/0/pkg/files/MyPowerTools/state/x.PDF", "pdf")]
    [InlineData("C:\\Users\\me\\Desktop\\Report.TXT", "txt")]
    [InlineData("archive.tar.gz", "gz")]
    [InlineData(".gitignore", null)]
    [InlineData("trailing.", null)]
    [InlineData("name.abcdefghijklmnop", null)]
    [InlineData("name.a1", "a1")]
    [InlineData("name.a-b", null)]
    public void ExtensionExtractionIsBounded(string fileName, string? expected)
    {
        Assert.Equal(expected, SharedFileMime.Extension(fileName));
    }

    [Theory]
    [InlineData("text/plain", "text/plain")]
    [InlineData("  TEXT/PLAIN  ", "text/plain")]
    [InlineData("text/plain; charset=utf-8", "text/plain")]
    [InlineData("image/svg+xml", "image/svg+xml")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("plain", null)]
    [InlineData("/plain", null)]
    [InlineData("text/", null)]
    [InlineData("text/plain/extra", null)]
    [InlineData("text/pl ain", null)]
    [InlineData("text/\"plain", null)]
    public void NormaliseAcceptsOnlyBareTypes(string candidate, string? expected)
    {
        Assert.Equal(expected, SharedFileMime.Normalize(candidate));
    }
}
