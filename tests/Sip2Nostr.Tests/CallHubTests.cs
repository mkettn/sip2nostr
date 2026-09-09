using Serilog;
using SIPSorceryMedia.Abstractions;
using Sip2Nostr.Hub;
using Xunit;

namespace Sip2Nostr.Tests;

public class CallHubTests
{
    [Fact]
    public async Task OffersTheCallToEachSinkUntilOneHandlesIt()
    {
        var declining = new FakeSink(handles: false);
        var handling = new FakeSink(handles: true);
        var never = new FakeSink(handles: true);
        var call = new FakeCall();

        await RouteAsync(call, declining, handling, never);

        Assert.Equal(1, declining.Offers);
        Assert.Equal(1, handling.Offers);
        Assert.Equal(0, never.Offers);
    }

    [Fact]
    public async Task NeverAnswersACallItself()
    {
        var call = new FakeCall();

        await RouteAsync(call, new FakeSink(handles: false), new FakeSink(handles: true));

        Assert.Equal(0, call.Answers);
    }

    [Fact]
    public async Task LeavesAnUnhandledCallUpUntilTheCallerHangsUp()
    {
        var call = new FakeCall();

        var routing = RouteAsync(call, new FakeSink(handles: false));

        Assert.False(routing.IsCompleted);
        call.RemoteHangUp();
        await routing;
        Assert.Equal(1, call.Hangups);
    }

    [Fact]
    public async Task HangsUpAfterASinkThrows()
    {
        var call = new FakeCall();

        await RouteAsync(call, new ThrowingSink());

        Assert.Equal(1, call.Hangups);
    }

    [Fact]
    public async Task DrainAsyncReturnsImmediatelyWhenNothingIsInFlight()
    {
        var hub = new CallHub([], new LoggerConfiguration().CreateLogger());

        var drain = hub.DrainAsync(TimeSpan.FromSeconds(5));

        Assert.True(drain.IsCompleted);
        await drain;
    }

    [Fact]
    public async Task DrainAsyncWaitsForAnInFlightCallToFinish()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = new FakeCall();
        var source = new FakeSource();
        var hub = new CallHub([new GatedSink(gate.Task)], new LoggerConfiguration().CreateLogger());
        hub.Attach(source, CancellationToken.None);

        var routing = source.RaiseIncomingCallAsync(call.Call);
        var drain = hub.DrainAsync(TimeSpan.FromSeconds(5));

        Assert.False(drain.IsCompleted);
        gate.SetResult();
        await drain;
        await routing;
    }

    [Fact]
    public async Task DrainAsyncGivesUpAfterTheGracePeriod()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = new FakeCall();
        var source = new FakeSource();
        var hub = new CallHub([new GatedSink(gate.Task)], new LoggerConfiguration().CreateLogger());
        hub.Attach(source, CancellationToken.None);

        var routing = source.RaiseIncomingCallAsync(call.Call);
        await hub.DrainAsync(TimeSpan.FromMilliseconds(20));

        // DrainAsync gave up on its own grace period; the call underneath
        // is still running regardless.
        Assert.False(routing.IsCompleted);
        gate.SetResult();
        await routing;
    }

    private static Task RouteAsync(FakeCall call, params ICallSink[] sinks)
    {
        var source = new FakeSource();
        var hub = new CallHub(sinks, new LoggerConfiguration().CreateLogger());
        hub.Attach(source, CancellationToken.None);
        return source.RaiseIncomingCallAsync(call.Call);
    }

    private sealed class FakeSource : ICallSource
    {
        public event Func<Call, Task>? OnIncomingCall;

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RaiseIncomingCallAsync(Call call) => OnIncomingCall!.Invoke(call);
    }

    private sealed class FakeSink(bool handles) : ICallSink
    {
        public int Offers { get; private set; }

        public Task<bool> TryHandleAsync(Call call, CancellationToken ct)
        {
            Offers++;
            return Task.FromResult(handles);
        }
    }

    private sealed class ThrowingSink : ICallSink
    {
        public Task<bool> TryHandleAsync(Call call, CancellationToken ct) =>
            throw new InvalidOperationException("sink failed");
    }

    private sealed class GatedSink(Task gate) : ICallSink
    {
        public async Task<bool> TryHandleAsync(Call call, CancellationToken ct)
        {
            await gate;
            return true;
        }
    }

    private sealed class FakeCall
    {
        private readonly TaskCompletionSource _hangupTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Answers { get; private set; }

        public int Hangups { get; private set; }

        public Call Call { get; }

        public FakeCall()
        {
            Call = new Call(
                "call-id",
                "+4900000000",
                "line",
                new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA),
                new FakeAudio(),
                () =>
                {
                    Answers++;
                    return Task.FromResult(true);
                },
                () =>
                {
                    Hangups++;
                    return Task.CompletedTask;
                },
                _hangupTcs.Task);
        }

        public void RemoteHangUp() => _hangupTcs.SetResult();
    }

    private sealed class FakeAudio : ICallAudio
    {
        public event Action<RtpAudioFrame>? OnAudioReceived;

        public void Send(RtpAudioFrame frame) => OnAudioReceived?.Invoke(frame);

        public void SendEncodedSample(uint durationRtpUnits, byte[] sample)
        {
        }
    }
}
