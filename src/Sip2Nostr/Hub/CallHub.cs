using Serilog;

namespace Sip2Nostr.Hub;

// Routes every call from every attached source through the same ordered
// sink chain - see docs/hub-architecture.md for the design.
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
        catch (Exception exception)
        {
            logger.Error(exception, "Unhandled exception routing call {CallId} from {CallerNumber}.", call.CallId, call.CallerNumber);
        }
        finally
        {
            await call.HangupAsync();
        }
    }
}
