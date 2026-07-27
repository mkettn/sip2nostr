using Serilog;
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
    private readonly ILogger _logger;

    public ConfigCallerListProvider(CallerListConfig config, ILogger logger)
    {
        _blacklist = config.Blacklist.Select(PhoneNumberNormalizer.Normalize).ToHashSet();
        _whitelist = config.Whitelist.Select(PhoneNumberNormalizer.Normalize).ToHashSet();
        _logger = logger;
        _logger.Information(
            "Loaded caller list: {BlacklistCount} blacklist entries, {WhitelistCount} whitelist entries.",
            _blacklist.Count,
            _whitelist.Count);
    }

    public Task<CallerListDecision> EvaluateAsync(string normalizedNumber, CancellationToken ct)
    {
        if (_blacklist.Contains(normalizedNumber))
        {
            _logger.Information("Caller {CallerNumber} matched the blacklist; denying.", normalizedNumber);
            return Task.FromResult(CallerListDecision.Deny);
        }

        if (_whitelist.Count > 0)
        {
            var onWhitelist = _whitelist.Contains(normalizedNumber);
            _logger.Information(
                "Whitelist is non-empty; caller {CallerNumber} is {WhitelistStatus}.",
                normalizedNumber,
                onWhitelist ? "on the whitelist - allowing" : "not on the whitelist - denying");
            return Task.FromResult(onWhitelist ? CallerListDecision.Allow : CallerListDecision.Deny);
        }

        _logger.Information(
            "Caller {CallerNumber} matched neither list; whitelist is empty, so allowing by default.",
            normalizedNumber);
        return Task.FromResult(CallerListDecision.NoMatch);
    }
}
