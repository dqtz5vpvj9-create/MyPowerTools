namespace FileTransfer.Core.Tests;

public sealed class UrlSafeBase64Tests
{
    [Theory]
    [InlineData(new byte[0], "")]
    [InlineData(new byte[] { 0xfb }, "-w")]
    [InlineData(new byte[] { 0xfb, 0xff }, "-_8")]
    [InlineData(new byte[] { 0xfb, 0xff, 0xfe }, "-__-")]
    public void RoundTripsUnpaddedAndPaddedForms(byte[] raw, string expectedEncoded)
    {
        var encoded = UrlSafeBase64.EncodeToString(raw);
        Assert.Equal(expectedEncoded, encoded);
        Assert.Equal(raw, UrlSafeBase64.DecodeFromChars(encoded));

        if (encoded.Length % 4 != 0)
        {
            var padded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4), '=');
            Assert.Equal(raw, UrlSafeBase64.DecodeFromChars(padded));
        }
    }

    [Theory]
    [InlineData("a")]
    [InlineData("-+8=")]
    [InlineData("-/8=")]
    [InlineData("ab=c")]
    [InlineData("a===")]
    [InlineData("ab cd")]
    public void RejectsInvalidBase64UrlInputs(string malformed)
    {
        Assert.Throws<FormatException>(() => UrlSafeBase64.DecodeFromChars(malformed));
    }
}
