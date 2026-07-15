using System.Text;

namespace Sip2Nostr.CallerList;

// Normalizes a raw SIP From-header user part (e.g. "+49 30 12345",
// "0049-30-12345", or a tel: URI's user part with a trailing
// ";phone-context=..." suffix) into digits-plus-optional-leading-"+", so the
// same caller isn't treated as a different identity depending on which
// prefix/formatting convention the SIP trunk happens to send.
public static class PhoneNumberNormalizer
{
    public static string Normalize(string rawNumber)
    {
        var user = rawNumber.Split(';')[0];

        var digits = new StringBuilder();
        var sawLeadingPlus = false;
        foreach (var c in user)
        {
            if (c == '+' && digits.Length == 0)
            {
                digits.Append('+');
                sawLeadingPlus = true;
                continue;
            }

            if (char.IsDigit(c))
            {
                digits.Append(c);
            }
        }

        var result = digits.ToString();
        if (!sawLeadingPlus && result.StartsWith("00", StringComparison.Ordinal))
        {
            result = "+" + result[2..];
        }

        return result;
    }
}
