namespace MyPowerTools.Platform.Android.Tests;

public sealed class DownloadPublicationTests
{
    [Fact]
    public void Sanitize_keeps_the_file_name_and_strips_directories_and_control_characters()
    {
        Assert.Equal("report.pdf", DownloadPublication.SanitizeDisplayName("/data/user/0/com.mypowertools.android/cache/incoming/report.pdf"));
        Assert.Equal("a_b.txt", DownloadPublication.SanitizeDisplayName(@"C:\temp\a:b.txt"));
        Assert.Equal("na_me.txt", DownloadPublication.SanitizeDisplayName("na\0me.txt"));
    }

    [Fact]
    public void Sanitize_never_returns_an_empty_display_name()
    {
        Assert.Equal("download", DownloadPublication.SanitizeDisplayName("..."));
        Assert.Equal("download", DownloadPublication.SanitizeDisplayName("/tmp/   "));
    }

    [Fact]
    public void Sanitize_truncates_long_names_and_keeps_the_extension()
    {
        var name = DownloadPublication.SanitizeDisplayName(new string('x', 400) + ".pdf");

        Assert.Equal(180, name.Length);
        Assert.EndsWith(".pdf", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Mime_type_is_resolved_from_the_lower_case_extension_only()
    {
        var requested = new List<string>();
        var mime = DownloadPublication.MimeTypeFor("Holiday.Photo.JPG", extension =>
        {
            requested.Add(extension);
            return extension == "jpg" ? "image/jpeg" : null;
        });

        Assert.Equal("image/jpeg", mime);
        Assert.Equal(new[] { "jpg" }, requested);
    }

    [Fact]
    public void Unknown_or_missing_extensions_fall_back_to_octet_stream()
    {
        Assert.Equal(DownloadPublication.FallbackMimeType, DownloadPublication.MimeTypeFor("archive.unknown-ext"));
        Assert.Equal(DownloadPublication.FallbackMimeType, DownloadPublication.MimeTypeFor("README"));
        Assert.Equal(DownloadPublication.FallbackMimeType, DownloadPublication.MimeTypeFor("file.bin", _ => null));
        Assert.Equal(DownloadPublication.FallbackMimeType, DownloadPublication.MimeTypeFor("file.bin", _ => "   "));
    }

    [Fact]
    public void Colliding_names_get_a_numeric_suffix_and_keep_the_extension()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "report.pdf", "report (1).pdf" };

        Assert.Equal("report (2).pdf", DownloadPublication.UniqueDisplayName("report.pdf", taken.Contains));
        Assert.Equal("report.pdf", DownloadPublication.UniqueDisplayName("report.pdf", _ => false));
    }

    [Fact]
    public void Downloads_publish_under_the_mpt_folder()
    {
        Assert.Equal("Download/MPT", DownloadPublication.RelativeDirectory);
    }
}
