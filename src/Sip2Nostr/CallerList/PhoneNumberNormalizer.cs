using System.Text;

namespace Sip2Nostr.CallerList;

// Normalizes a raw SIP From-header user part (e.g. "+49 30 12345",
// "0049-30-12345", or a tel: URI's user part with a trailing
// ";phone-context=..." suffix) into plain digits, so the same caller isn't
// treated as a different identity depending on which prefix/formatting
// convention the SIP trunk happens to send. The result never carries a
// leading "+": keeping it distinguished "+49..." from "49..." even though
// they're the same number whenever a trunk omits the international-prefix
// marker, which silently broke blacklist/whitelist matching.
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
            result = result[2..];
        }

        return result;
    }
}
