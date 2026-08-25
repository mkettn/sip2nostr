using Serilog;

namespace Sip2Nostr.Hub;

// Routes every call from every attached source through the same ordered
// sink chain (e.g. NosCallSink, then VoicemailSink) - the first sink that
// returns true wins. If none does, the call is left connected until the
// caller hangs up (unchanged from the pre-hub behavior for [nostr].enabled
// = false or [voicemail].enabled = false with no other sink configured).
public sealed class CallHub(IReadOnlyList<ICallSink> sinks, ILogger logger)
{
    public void Attach(ICallSource source, CancellationToken ct)
    {
        source.OnIncomingCall += call => HandleAsync(call, ct);
    }

    private async Task HandleAsync(Call call, CancellationToken ct)
    {
        try
        {
            foreach (var sink in sinks)
            {
                if (await sink.TryHandleAsync(call, ct))
                {
                    return;
                }
            }

            logger.Information(
                "No sink handled call {CallId} from {CallerNumber}; leaving it connected until the caller hangs up.",
                call.CallId,
                call.CallerNumber);
            await call.WhenRemoteHungUp;
        }
        finally
        {
            await call.HangupAsync();
        }
    }
}
