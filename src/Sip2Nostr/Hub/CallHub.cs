using System.Collections.Concurrent;
using Serilog;

namespace Sip2Nostr.Hub;

// Routes every call from every attached source through the same ordered
// sink chain - see docs/hub-architecture.md for the design.
public sealed class CallHub(IReadOnlyList<ICallSink> sinks, ILogger logger)
{
    // Every in-flight HandleAsync task, so shutdown can wait for calls
    // already being wound down (see DrainAsync) instead of abandoning
    // them mid-flight the instant the caller's own CancellationToken is
    // cancelled - see docs/propagating-to-nostr.md's "cancellation
    // without a join" note.
    private readonly ConcurrentDictionary<Task, byte> _pending = new();

    public void Attach(ICallSource source, CancellationToken ct)
    {
        source.OnIncomingCall += call => TrackedHandleAsync(call, ct);
    }

    // Waits, up to grace, for calls already in flight to finish their own
    // wind-down (a last hangup published over Nostr, a recording flushed
    // to disk) rather than exiting out from under them - see
    // docs/propagating-to-nostr.md. A no-op once every call has already
    // finished, which is the common case for a shutdown that isn't racing
    // an active call.
    public async Task DrainAsync(TimeSpan grace)
    {
        var pending = _pending.Keys.ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        logger.Information(
            "Waiting up to {GraceSeconds}s for {PendingCount} in-flight call(s) to finish shutting down.",
            grace.TotalSeconds,
            pending.Length);
        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(grace));
    }

    // Returns the same Task HandleAsync produces - callers up the chain
    // (SipCallSource) still await it exactly as before - while also
    // registering it so DrainAsync can join it later. Removed from
    // _pending via a continuation rather than a try/finally around the
    // await, so registering costs nothing on the hot path and this stays
    // a thin wrapper rather than a second copy of HandleAsync's body.
    private Task TrackedHandleAsync(Call call, CancellationToken ct)
    {
        var task = HandleAsync(call, ct);
        _pending[task] = 0;
        _ = task.ContinueWith(
            t => _pending.TryRemove(t, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
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

            // A sink that declines never answers (see Call.cs), so an
            // unhandled call is still ringing rather than connected to
            // silence.
            logger.Information(
                "No sink handled call {CallId} from {CallerNumber}; leaving it ringing until the caller hangs up.",
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
