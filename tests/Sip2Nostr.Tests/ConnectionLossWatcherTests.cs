using SIPSorcery.Net;
using Sip2Nostr.Sinks;
using Xunit;

namespace Sip2Nostr.Tests;

public class ConnectionLossWatcherTests
{
    [Fact]
    public void FailedCompletesImmediately()
    {
        using var watcher = new ConnectionLossWatcher(TimeSpan.FromSeconds(30));

        watcher.OnStateChange(RTCPeerConnectionState.failed);

        Assert.True(watcher.WhenConnectionLost.IsCompleted);
    }

    [Fact]
    public async Task DisconnectedCompletesAfterGraceElapses()
    {
        using var watcher = new ConnectionLossWatcher(TimeSpan.FromMilliseconds(20));

        watcher.OnStateChange(RTCPeerConnectionState.disconnected);

        Assert.False(watcher.WhenConnectionLost.IsCompleted);
        await watcher.WhenConnectionLost;
    }

    [Fact]
    public async Task RecoveringToConnectedCancelsTheGraceTimer()
    {
        using var watcher = new ConnectionLossWatcher(TimeSpan.FromMilliseconds(20));

        watcher.OnStateChange(RTCPeerConnectionState.disconnected);
        watcher.OnStateChange(RTCPeerConnectionState.connected);

        await Task.Delay(TimeSpan.FromMilliseconds(60));
        Assert.False(watcher.WhenConnectionLost.IsCompleted);
    }

    [Fact]
    public void IgnoresUnrelatedStates()
    {
        using var watcher = new ConnectionLossWatcher(TimeSpan.FromMilliseconds(20));

        watcher.OnStateChange(RTCPeerConnectionState.@new);
        watcher.OnStateChange(RTCPeerConnectionState.connecting);
        watcher.OnStateChange(RTCPeerConnectionState.closed);

        Assert.False(watcher.WhenConnectionLost.IsCompleted);
    }

    [Fact]
    public async Task DisposeCancelsAPendingGraceTimerWithoutCompleting()
    {
        var watcher = new ConnectionLossWatcher(TimeSpan.FromMilliseconds(20));
        watcher.OnStateChange(RTCPeerConnectionState.disconnected);

        watcher.Dispose();

        await Task.Delay(TimeSpan.FromMilliseconds(60));
        Assert.False(watcher.WhenConnectionLost.IsCompleted);
    }

    [Fact]
    public void StateChangesAfterDisposeAreIgnored()
    {
        var watcher = new ConnectionLossWatcher(TimeSpan.FromSeconds(30));
        watcher.Dispose();

        watcher.OnStateChange(RTCPeerConnectionState.failed);

        Assert.False(watcher.WhenConnectionLost.IsCompleted);
    }
}
