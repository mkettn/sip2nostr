using Nostr.Sdk;
using Serilog;

namespace Sip2Nostr.Signaling;

// Uses TryConnect(), not Connect(): the latter is fire-and-forget and
// returns before any attempt necessarily started (confirmed against
// rust-nostr's source, sdk/src/pool/mod.rs), while TryConnect() actually
// awaits each relay within the timeout and reports the real per-relay
// failure reason.
internal static class RelayConnector
{
    // The returned relays are reachable pool-wide, not scoped to
    // `relays` - a client that already had others connected includes
    // those too. Intersect against `relays` if only that set matters.
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
