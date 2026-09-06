using ImeManager.MyPowerTools;

namespace ImeManager.Tests;

public sealed class RegistryValueFormatTests
{
    [Theory]
    [InlineData("0409:00000409")]
    [InlineData("0804:00000804")]
    [InlineData("040A:0000040A")]
    [InlineData("0411:E0010411")]
    public void preload_values_round_trip_through_the_hexadecimal_reader(string tipString)
    {
        Assert.True(ParsedTipString.TryParse(tipString, out var parsed));
        var value = WindowsInputMethodPlatform.PreloadValue(parsed);
        Assert.Equal(8, value.Length);
        Assert.True(WindowsInputMethodPlatform.TryParsePreloadValue(value, out var canonical));
        Assert.Equal(parsed.Canonical, canonical);
    }

    [Fact]
    public void preload_values_for_text_services_carry_the_language_identifier()
    {
        Assert.True(ParsedTipString.TryParse(
            "0804:{A028AE76-01B1-46C2-99C4-ACD9858AE02F}{B5FE1F02-D5F2-4445-9C03-C568F23C99A1}",
            out var tip));
        var value = WindowsInputMethodPlatform.PreloadValue(tip);
        Assert.Equal("00000804", value);
        Assert.True(WindowsInputMethodPlatform.TryParsePreloadValue(value, out var canonical));
        Assert.Equal("0804:00000804", canonical);
    }

    [Fact]
    public void decimal_preload_values_do_not_survive_the_round_trip()
    {
        Assert.True(ParsedTipString.TryParse("0409:00000409", out var parsed));
        var decimalValue = parsed.KeyboardLayoutId.ToString("00000000", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("00001033", decimalValue);
        Assert.True(WindowsInputMethodPlatform.TryParsePreloadValue(decimalValue, out var canonical));
        Assert.NotEqual(parsed.Canonical, canonical);
    }

    [Theory]
    [InlineData((ushort)0x0409)]
    [InlineData((ushort)0x0804)]
    [InlineData((ushort)0x0C0A)]
    public void sort_order_language_values_round_trip_through_the_hexadecimal_reader(ushort languageId)
    {
        var value = WindowsInputMethodPlatform.SortOrderLanguageValue(languageId);
        Assert.Equal(8, value.Length);
        Assert.True(WindowsInputMethodPlatform.TryParseLanguageKey(value, out var restored));
        Assert.Equal(languageId, restored);
    }
}
