using Nostr.Sdk;
using Serilog;

namespace Sip2Nostr.Signaling;

// A relay rejects an event with `OK: false` over an otherwise-healthy
// connection, which doesn't throw - discarding SendEventOutput would
// report a rejected publish as a success.
internal static class PublishOutcome
{
    public static void ThrowIfFailed(ILogger logger, string what, SendEventOutput output)
    {
        if (output.success.Count == 0)
        {
            var reasons = string.Join("; ", output.failed.Select(f => $"{f.Key}: {f.Value}"));
            logger.Error("Failed to publish {What} to any relay: {Reasons}", what, reasons);
            throw new InvalidOperationException($"No relay accepted {what}: {reasons}");
        }

        if (output.failed.Count > 0)
        {
            var reasons = string.Join("; ", output.failed.Select(f => $"{f.Key}: {f.Value}"));
            logger.Warning(
                "{What} reached {SuccessCount} relay(s) but was rejected by others: {Reasons}",
                what,
                output.success.Count,
                reasons);
        }
    }
}
