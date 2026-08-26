using Serilog;
using SIPSorceryMedia.Abstractions;
using Sip2Nostr.CallerList;
using Sip2Nostr.Config;
using Sip2Nostr.Hub;
using Tmds.DBus;

namespace Sip2Nostr.Modem;

// Registers with ModemManager over D-Bus and raises OnIncomingCall for
// CallHub to route once a call on the attached device is accepted and its
// audio is bridged - the modem/D-Bus equivalent of Sip/SipCallSource for a
// directly attached phone/modem device instead of a SIP trunk. CallHub and
// its sinks (NosCallSink, VoicemailSink, LocalTestAudioSink) don't know or
// care which source a Call came from - see docs/hub-architecture.md - so
// this owns only what's specific to a modem: finding/watching it via
// ModemManagerClient, and bridging its audio via ModemCallAudio, since
// unlike the SIP leg that audio never arrives RTP-shaped.
public sealed class ModemCallSource(AppConfig config, ModemConfig modemConfig, ILogger logger) : ICallSource, IAsyncDisposable
{
    // Fixed, not negotiated: there's no SDP offer to pick a codec from on
    // this leg (there is no SIP/WebRTC signaling at this layer at all).
    private static readonly AudioFormat SelectedAudioFormat = new(SDPWellKnownMediaFormatsEnum.PCMA);

    private readonly CallerListGate _callerListGate = new(
        [new ConfigCallerListProvider(config.CallerList, logger.ForContext<ConfigCallerListProvider>())],
        logger.ForContext<CallerListGate>());
    private ModemManagerClient? _modemManager;

    public event Func<Call, Task>? OnIncomingCall;

    public async Task StartAsync(CancellationToken ct)
    {
        _modemManager = new ModemManagerClient(modemConfig, logger.ForContext<ModemManagerClient>());
        _modemManager.OnIncomingCall += call => _ = HandleIncomingCallSafeAsync(call, ct);
        await _modemManager.StartAsync(ct);
        logger.Information("Modem call source started for line {LineLabel}.", modemConfig.Label);
    }

    private async Task HandleIncomingCallSafeAsync(ModemIncomingCall incoming, CancellationToken ct)
    {
        try
        {
            await AcceptAndRouteCallAsync(incoming, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.Information("Modem call handling stopped because shutdown was requested.");
        }
        catch (Exception exception)
        {
            logger.Error(exception, "Modem call handling failed for {CallPath}.", incoming.CallPath);
        }
    }

    // Accepts the modem leg and hands the resulting Call to CallHub - see
    // Call.cs for why there's no separate "answer" step at the hub level.
    private async Task AcceptAndRouteCallAsync(ModemIncomingCall incoming, CancellationToken ct)
    {
        var callerNumber = PhoneNumberNormalizer.Normalize(incoming.Number ?? string.Empty);
        logger.Information(
            "Incoming modem call from {CallerNumber} on {CallPath}; line: {LineLabel}.",
            callerNumber,
            incoming.CallPath,
            modemConfig.Label);

        if (!await _callerListGate.IsAllowedAsync(callerNumber, ct))
        {
            logger.Information("Caller {CallerNumber} is not allowed to reach this line; rejecting.", callerNumber);
            await HangupSafeAsync(incoming.Call, incoming.CallPath);
            return;
        }

        var activeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hangupTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stateWatch = await incoming.Call.WatchStateChangedAsync(
            change => OnStateChanged(incoming.CallPath, change, activeTcs, hangupTcs),
            exception => logger.Warning(exception, "Modem call state watch failed for {CallPath}.", incoming.CallPath));
        using var ctReg = ct.Register(() => hangupTcs.TrySetResult());

        logger.Information("Accepting modem call on {CallPath}.", incoming.CallPath);
        await incoming.Call.AcceptAsync();

        if (await Task.WhenAny(activeTcs.Task, hangupTcs.Task) == hangupTcs.Task)
        {
            logger.Information("Modem call ended before becoming active; nothing to route.");
            return;
        }

        var callId = Guid.NewGuid().ToString();
        logger.Information(
            "Modem call {CallPath} active; opening ALSA device {AlsaDevice} for call-id {CallId}.",
            incoming.CallPath,
            modemConfig.AlsaDevice,
            callId);
        var audio = new ModemCallAudio(modemConfig.AlsaDevice, SelectedAudioFormat, logger.ForContext<ModemCallAudio>());

        var call = new Call(
            callId,
            callerNumber,
            modemConfig.Label,
            SelectedAudioFormat,
            audio,
            () => HangupSafeAsync(incoming.Call, incoming.CallPath),
            hangupTcs.Task);

        try
        {
            // Mirrors SipCallSource: await every subscriber explicitly
            // rather than Invoke(), which on a multicast delegate only
            // awaits whichever Task the last subscriber returned.
            if (OnIncomingCall is not null)
            {
                foreach (var handler in OnIncomingCall.GetInvocationList())
                {
                    await ((Func<Call, Task>)handler)(call);
                }
            }
        }
        finally
        {
            await audio.DisposeAsync();
        }
    }

    private void OnStateChanged(
        ObjectPath callPath,
        (int Old, int New, uint Reason) change,
        TaskCompletionSource activeTcs,
        TaskCompletionSource hangupTcs)
    {
        logger.Information(
            "Modem call {CallPath} state changed: {Old} -> {New} (reason {Reason}).",
            callPath,
            change.Old,
            change.New,
            change.Reason);

        if (change.New == MMCallState.Active)
        {
            activeTcs.TrySetResult();
        }
        else if (change.New == MMCallState.Terminated)
        {
            hangupTcs.TrySetResult();
        }
    }

    // CallHub always calls Call.HangupAsync once a sink is done, even if
    // the call already ended remotely (e.g. hangupTcs already completed
    // because ModemManager reported Terminated) - swallow that case rather
    // than letting a double-hangup D-Bus error propagate out of CallHub.
    private async Task HangupSafeAsync(ICall call, ObjectPath callPath)
    {
        try
        {
            await call.HangupAsync();
        }
        catch (Exception exception)
        {
            logger.Debug(exception, "Hangup for modem call {CallPath} failed (likely already terminated).", callPath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_modemManager is not null)
        {
            await _modemManager.DisposeAsync();
        }
    }
}
