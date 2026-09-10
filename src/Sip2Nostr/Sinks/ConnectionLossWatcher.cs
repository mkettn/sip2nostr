using SIPSorcery.Net;

namespace Sip2Nostr.Sinks;

// Debounces RTCPeerConnection.onconnectionstatechange so a momentary ICE
// blip (a brief Wi-Fi drop, a NAT rebind) doesn't end a bridged call the
// instant the connection state flaps to "disconnected" - see
// docs/propagating-to-nostr.md's "A callee that vanishes without
// signaling" note. Only a "disconnected" that never recovers to
// "connected" within grace, or an outright "failed" (the ICE agent's own
// terminal give-up, which spec-wise only happens after it has already
// exhausted its own connectivity checks, so it needs no further
// debouncing), completes WhenConnectionLost.
public sealed class ConnectionLossWatcher(TimeSpan grace) : IDisposable
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _lostTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _graceCts;
    private bool _disposed;

    public Task WhenConnectionLost => _lostTcs.Task;

    public void OnStateChange(RTCPeerConnectionState state)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            switch (state)
            {
                case RTCPeerConnectionState.failed:
                    CancelGraceTimer();
                    _lostTcs.TrySetResult();
                    break;

                case RTCPeerConnectionState.disconnected:
                    // Already waiting one out - a state that keeps
                    // reporting "disconnected" doesn't get a fresh grace
                    // period each time, just the one already running.
                    if (_graceCts is null)
                    {
                        var cts = new CancellationTokenSource();
                        _graceCts = cts;
                        _ = Task.Delay(grace, cts.Token).ContinueWith(
                            t =>
                            {
                                if (!t.IsCanceled)
                                {
                                    _lostTcs.TrySetResult();
                                }
                            },
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }

                    break;

                case RTCPeerConnectionState.connected:
                    // Recovered from a prior "disconnected" - the call's
                    // still good.
                    CancelGraceTimer();
                    break;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CancelGraceTimer();
        }
    }

    private void CancelGraceTimer()
    {
        _graceCts?.Cancel();
        _graceCts?.Dispose();
        _graceCts = null;
    }
}
