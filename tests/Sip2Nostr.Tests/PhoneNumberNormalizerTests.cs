using Sip2Nostr.CallerList;
using Xunit;

namespace Sip2Nostr.Tests;

public class PhoneNumberNormalizerTests
{
    [Theory]
    [InlineData("+49 30 12345", "493012345")]
    [InlineData("0049-30-12345", "493012345")]
    [InlineData("493012345", "493012345")]
    [InlineData("49 30 12345", "493012345")]
    [InlineData("+493012345", "493012345")]
    [InlineData("493012345;phone-context=+49", "493012345")]
    [InlineData("030 12 345", "03012345")]
    [InlineData("", "")]
    public void Normalize_ProducesPlainDigitsRegardlessOfPrefixFormat(string raw, string expected)
    {
        Assert.Equal(expected, PhoneNumberNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_SameNumberInDifferentFormats_ProducesIdenticalResult()
    {
        var withPlus = PhoneNumberNormalizer.Normalize("+49 30 12345");
        var withDoubleZero = PhoneNumberNormalizer.Normalize("0049-30-12345");
        var bareDigits = PhoneNumberNormalizer.Normalize("493012345");

        Assert.Equal(withPlus, withDoubleZero);
        Assert.Equal(withPlus, bareDigits);
    }
}
