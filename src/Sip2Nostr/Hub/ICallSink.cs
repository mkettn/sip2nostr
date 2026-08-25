namespace Sip2Nostr.Hub;

// Something CallHub can offer a call to. Returning true means it handled
// the call end-to-end (bridged it until hangup, or recorded a voicemail) -
// CallHub won't try any further sinks. Returning false means it declined
// (e.g. NosCallSink's ring timeout elapsed) and the next configured sink
// gets a turn.
public interface ICallSink
{
    Task<bool> TryHandleAsync(Call call, CancellationToken ct);
}
