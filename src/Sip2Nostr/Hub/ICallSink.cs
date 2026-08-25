namespace Sip2Nostr.Hub;

// Something CallHub can offer a call to - see docs/hub-architecture.md
// for the sink-chain contract (what true/false mean here).
public interface ICallSink
{
    Task<bool> TryHandleAsync(Call call, CancellationToken ct);
}
