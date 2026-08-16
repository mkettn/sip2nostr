using Nostr.Sdk;
using Serilog;

namespace Sip2Nostr.Signaling;

// Shared connect-and-log helper. Connect() is fire-and-forget - it kicks
// off each relay's connection loop and returns immediately, with no
// guarantee any attempt even started (confirmed against rust-nostr's
// source: sdk/src/pool/mod.rs). TryConnect() actually awaits a real
// per-relay connection attempt within the timeout and returns which
// relays succeeded/failed, with the real underlying error
// (DNS/TLS/refused/etc.) per failure - not just an opaque status enum.
internal static class RelayConnector
{
    // Returns the relays actually reachable, pool-wide - not scoped to
    // just the `relays` argument if the client already had others
    // connected. A caller that needs to know whether *its own* specific
    // relays connected, on a client whose pool may include others, must
    // intersect the result against its own list.
    public static async Task<List<RelayUrl>> ConnectAsync(Client client, List<RelayUrl> relays, TimeSpan timeout, ILogger logger)
    {
        foreach (var relay in relays)
        {
            await client.AddRelay(relay);
        }

        var output = await client.TryConnect(timeout);
        foreach (var failure in output.failed)
        {
            logger.Warning("Nostr relay {RelayUrl} failed to connect: {Reason}.", failure.Key, failure.Value);
        }

        return output.success;
    }
}
