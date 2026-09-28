using MyPowerTools.Android.Pairing;

namespace MyPowerTools.Android.Tests;

/// <summary>
/// The share -> activation mapping. A plain text share used to be dropped before it reached the file
/// assistant, and a share that carries text and files must arrive as one composer, never as two
/// competing activations.
/// </summary>
public sealed class ShareActivationTests
{
    private const string Secret = "correct horse battery staple";

    [Fact]
    public void Shared_text_becomes_the_assistant_composer_activation()
    {
        var activation = ShareActivation.TextActivation("看看这个 https://example.com/a?b=1&c=2");

        Assert.NotNull(activation);
        Assert.StartsWith(ShareActivation.AssistantUriPrefix, activation, StringComparison.Ordinal);
        Assert.Equal("看看这个 https://example.com/a?b=1&c=2", ShareActivation.ReadText(activation));
    }

    [Fact]
    public void Shared_text_can_never_break_out_of_the_query()
    {
        // A selection that looks like a query or a fragment must arrive as text, not as a second
        // parameter the assistant would then act on.
        var activation = ShareActivation.TextActivation("text=1&targetDeviceId=dev-1#frag")!;
        var uri = new Uri(activation);

        // The whole selection is one escaped value: no extra query parameter, no fragment.
        Assert.Equal(ShareActivation.AssistantUriPrefix, activation[..activation.IndexOf('?')]);
        Assert.Equal("text=1&targetDeviceId=dev-1#frag", ShareActivation.ReadText(activation));
        Assert.Single(uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("", uri.Fragment);
    }

    [Fact]
    public void Text_and_attachments_land_in_one_activation_list()
    {
        var activations = ShareActivation.BuildActivations(
            Secret,
            ["file:///cache/shares/1/a.jpg", "file:///cache/shares/2/b.pdf"]);

        Assert.Equal(3, activations.Count);
        Assert.Equal(Secret, ShareActivation.ReadText(activations[0]));
        Assert.Equal("file:///cache/shares/1/a.jpg", activations[1]);
        Assert.Equal("file:///cache/shares/2/b.pdf", activations[2]);
    }

    [Fact]
    public void An_attachment_only_share_has_no_text_activation()
    {
        var activations = ShareActivation.BuildActivations(null, ["file:///cache/shares/1/a.jpg"]);
        Assert.Single(activations);
        Assert.Null(ShareActivation.ReadText(activations[0]));
    }

    [Fact]
    public void Empty_and_whitespace_text_never_produces_an_activation()
    {
        Assert.Null(ShareActivation.TextActivation(null));
        Assert.Null(ShareActivation.TextActivation(""));
        Assert.True(ShareActivation.IsEmpty("", 0));
        Assert.True(ShareActivation.IsEmpty(null, 0));
        Assert.False(ShareActivation.IsEmpty("x", 0));
        Assert.False(ShareActivation.IsEmpty(null, 1));
    }

    [Fact]
    public void An_oversized_selection_is_refused_rather_than_truncated()
    {
        // Half a shared text with no notice would be worse than not delivering it: the caller can
        // tell the user instead.
        Assert.Null(ShareActivation.TextActivation(new string('x', ShareActivation.MaximumTextLength + 1)));
        Assert.NotNull(ShareActivation.TextActivation(new string('x', ShareActivation.MaximumTextLength)));
    }

    [Fact]
    public void A_share_summary_never_contains_the_shared_text()
    {
        var description = ShareActivation.Describe(hasText: true, attachmentCount: 2, "image/jpeg");

        Assert.DoesNotContain(Secret, description, StringComparison.Ordinal);
        Assert.Equal("text=yes attachments=2 type=image/jpeg", description);
        Assert.Equal("text=no attachments=0 type=(none)", ShareActivation.Describe(false, 0, null));
    }

    [Theory]
    [InlineData("mypowertools://file-assistant?text=hi", true)]
    [InlineData("mypowertools://file-assistant", true)]
    [InlineData("mpt://assistant/abc", true)]
    [InlineData("mpt://pair/abc", false)]
    [InlineData("mpt://assistantX/abc", false)]
    [InlineData(null, false)]
    public void Only_the_assistant_entry_points_resolve_to_the_assistant(string? uri, bool expected) =>
        Assert.Equal(expected, ShareActivation.IsAssistantEntryPoint(uri));

    [Fact]
    public void Reading_text_from_another_link_returns_nothing()
    {
        Assert.Null(ShareActivation.ReadText("mpt://assistant/abc"));
        Assert.Null(ShareActivation.ReadText("file:///cache/a.jpg"));
        Assert.Null(ShareActivation.ReadText(null));
    }
}
