using Sip2Nostr.Config;

namespace Sip2Nostr.CallerList;

// Config-backed blacklist/whitelist. Precedence: a blacklist match always
// denies. A non-empty whitelist switches to deny-by-default - only
// whitelisted numbers are allowed. An empty whitelist means blacklist-only
// mode: anything not blacklisted is NoMatch, left for CallerListGate to
// resolve to "allowed" by default.
public sealed class ConfigCallerListProvider : ICallerListProvider
{
    private readonly HashSet<string> _blacklist;
    private readonly HashSet<string> _whitelist;

    public ConfigCallerListProvider(CallerListConfig config)
    {
        _blacklist = config.Blacklist.Select(PhoneNumberNormalizer.Normalize).ToHashSet();
        _whitelist = config.Whitelist.Select(PhoneNumberNormalizer.Normalize).ToHashSet();
    }

    public Task<CallerListDecision> EvaluateAsync(string normalizedNumber, CancellationToken ct)
    {
        if (_blacklist.Contains(normalizedNumber))
        {
            return Task.FromResult(CallerListDecision.Deny);
        }

        if (_whitelist.Count > 0)
        {
            return Task.FromResult(_whitelist.Contains(normalizedNumber)
                ? CallerListDecision.Allow
                : CallerListDecision.Deny);
        }

        return Task.FromResult(CallerListDecision.NoMatch);
    }
}
