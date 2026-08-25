using Sip2Nostr.CallerList;
using Xunit;

namespace Sip2Nostr.Tests;

public class PhoneNumberNormalizerTests
{
    [Theory]
    [InlineData("+49 30 12345", "493012345")]
    [InlineData("0049-30-12345", "493012345")]
    [InlineData("493012345", "493012345")]
    public void Normalize_SamePhysicalNumberInDifferentFormats_ProducesSameResult(string raw, string expected)
    {
        Assert.Equal(expected, PhoneNumberNormalizer.Normalize(raw));
    }

    [Fact]
    public void Normalize_MoreThan32Digits_TruncatesTo32()
    {
        var raw = new string('1', 100);
        var result = PhoneNumberNormalizer.Normalize(raw);
        Assert.Equal(32, result.Length);
        Assert.Equal(new string('1', 32), result);
    }
}
