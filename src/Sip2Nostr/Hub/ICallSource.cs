namespace Sip2Nostr.Hub;

// Something that can produce calls for CallHub to route - today, SIP.
// A future modem/D-Bus source implements this the same way. Outbound
// dialing (see docs/hub-architecture.md) would be a second method added
// here later - a source that can only receive doesn't need to change to
// accommodate it.
public interface ICallSource
{
    event Func<Call, Task> OnIncomingCall;

    Task StartAsync(CancellationToken ct);
}
