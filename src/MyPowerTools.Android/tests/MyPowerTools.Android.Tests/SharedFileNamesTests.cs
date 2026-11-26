using MyPowerTools.Android.Pairing;

namespace MyPowerTools.Android.Tests;

public sealed class SharedFileNamesTests
{
    [Theory]
    [InlineData("报告.pdf", "报告.pdf")]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("  spaced.txt  ", "spaced.txt")]
    public void An_ordinary_name_is_kept(string reported, string expected) =>
        Assert.Equal(expected, SharedFileNames.SafeLeafName(reported));

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\Windows\\System32\\cmd.exe", "cmd.exe")]
    [InlineData("/data/local/tmp/payload", "payload")]
    [InlineData("nested/folder/notes.md", "notes.md")]
    [InlineData("..", "shared-file")]
    [InlineData(".", "shared-file")]
    [InlineData("", "shared-file")]
    [InlineData(null, "shared-file")]
    public void A_provider_name_can_never_escape_the_share_folder(string? reported, string expected) =>
        Assert.Equal(expected, SharedFileNames.SafeLeafName(reported));

    [Fact]
    public void A_control_character_becomes_an_underscore()
    {
        Assert.Equal("bad_name.txt", SharedFileNames.SafeLeafName("bad\u0000name.txt"));
    }

    [Fact]
    public void An_over_long_name_is_truncated_but_keeps_its_extension()
    {
        var name = new string('a', 400) + ".zip";
        var safe = SharedFileNames.SafeLeafName(name);

        Assert.True(safe.Length <= 120, $"expected a bounded name, got {safe.Length}");
        Assert.EndsWith(".zip", safe, StringComparison.Ordinal);
    }
}
