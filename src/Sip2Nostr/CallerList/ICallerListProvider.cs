namespace Sip2Nostr.CallerList;

public enum CallerListDecision
{
    Allow,
    Deny,
    NoMatch,
}

// A pure lookup against one source of allow/deny entries (config today;
// CardDAV or a Google contacts sync are natural future implementations).
// Precedence/default-policy is decided by CallerListGate, not here, so
// providers stay simple and swappable.
public interface ICallerListProvider
{
    Task<CallerListDecision> EvaluateAsync(string normalizedNumber, CancellationToken ct);
}
