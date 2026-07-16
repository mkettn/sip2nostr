using Serilog;

namespace Sip2Nostr.CallerList;

// The single dependency CallBridge takes - it never sees a concrete
// ICallerListProvider, so wiring up an additional source later (CardDAV,
// Google contacts) is purely a BridgeService construction-site change. Any
// provider denying blocks the call; otherwise it's allowed by default.
public sealed class CallerListGate
{
    private readonly List<ICallerListProvider> _providers;
    private readonly ILogger _logger;

    public CallerListGate(IEnumerable<ICallerListProvider> providers, ILogger logger)
    {
        _providers = providers.ToList();
        _logger = logger;
    }

    public async Task<bool> IsAllowedAsync(string normalizedNumber, CancellationToken ct)
    {
        foreach (var provider in _providers)
        {
            var decision = await provider.EvaluateAsync(normalizedNumber, ct);
            _logger.Information(
                "Caller list provider {Provider} evaluated {CallerNumber} as {Decision}.",
                provider.GetType().Name,
                normalizedNumber,
                decision);

            if (decision == CallerListDecision.Deny)
            {
                return false;
            }
        }

        return true;
    }
}
