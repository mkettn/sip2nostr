using Serilog;
using SIPSorcery.Net;
using Sip2Nostr.Config;
using Sip2Nostr.Hub;
using Sip2Nostr.Signaling;
using Sip2Nostr.Sip;

namespace Sip2Nostr.Sinks;

// Rings the callee over Nostr/WebRTC signaling (NIP-17 DMs carrying SDP
// offer/answer and ICE candidates - see NostrSignalingClient). The SIP
// leg is only answered once a real WebRTC answer comes back, so the
// caller hears ringback for as long as the callee's device is ringing. If
// ringTimeoutSeconds is set and nobody answers within it, declines so the
// next sink (typically VoicemailSink) gets a turn, leaving the call
// ringing for that sink to answer; if unset, rings until the caller hangs
// up. A hangup from the callee ends the call at either stage - declining
// straight away if it arrives while their device is still ringing, ending
// the SIP leg if it arrives mid-call. The caller giving up first is
// handled symmetrically: a Nostr hangup goes out so the callee's device
// stops ringing too, the same as an explicit ring-timeout decline.
public sealed class NosCallSink(
    NostrConfig nostrConfig,
    WebRtcConfig webRtcConfig,
    int? ringTimeoutSeconds,
    ILogger logger) : ICallSink
{
    // How long a bridged call's WebRTC connection can sit in
    // "disconnected" before ConnectionLossWatcher gives up on it
    // recovering - long enough to ride out a brief network blip, short
    // enough that a caller isn't stuck on dead air for minutes. A fixed
    // judgment call, not configurable, matching this file's own
    // ringTimeoutSeconds handling of "no answer" and NostrSignalingClient's
    // connect timeout.
    private const int ConnectionLossGraceSeconds = 15;

    public async Task<bool> TryHandleAsync(Call call, CancellationToken ct)
    {
        // Matched to the SIP leg's codec, not a fixed choice: RTP frames
        // are relayed unchanged between the two legs (see RtpAudioFrame),
        // so both sides must agree on the payload-type/codec mapping.
        var rtcConfig = new RTCConfiguration { iceServers = BuildIceServers() };
        var pc = new RTCPeerConnection(rtcConfig, 0, null, false);
        pc.addTrack(new MediaStreamTrack(call.AudioFormat, MediaStreamStatusEnum.SendRecv));
        logger.Information(
            "Created WebRTC peer connection with {IceServerCount} ICE server(s) for call {CallId}.",
            rtcConfig.iceServers.Count,
            call.CallId);

        var webRtcAudio = new RtpSessionCallAudio(pc);
        var forwardToWebRtc = webRtcAudio.Send;
        var forwardToSip = call.Audio.Send;

        void StopBridging()
        {
            call.Audio.OnAudioReceived -= forwardToWebRtc;
            webRtcAudio.OnAudioReceived -= forwardToSip;
        }

        call.Audio.OnAudioReceived += forwardToWebRtc;
        webRtcAudio.OnAudioReceived += forwardToSip;

        await using var signaling = new NostrSignalingClient(nostrConfig, call.CallId, logger.ForContext<NostrSignalingClient>());
        var calleeHangup = signaling.WhenCalleeHungUp;
        try
        {
            await signaling.ConnectAsync();
            logger.Information("Nostr signaling connected for call {CallId}.", call.CallId);

            pc.onicecandidate += candidate =>
            {
                _ = SendIceCandidateSafeAsync(signaling, candidate);
            };
            signaling.OnIceCandidateReceived(candidate =>
            {
                logger.Information("Received WebRTC ICE candidate over Nostr for call {CallId}.", call.CallId);
                pc.addIceCandidate(new RTCIceCandidateInit
                {
                    candidate = candidate.Candidate,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMLineIndex,
                });
            });

            var offer = pc.createOffer(null);
            await pc.setLocalDescription(offer);
            logger.Information("Sending WebRTC SDP offer over Nostr for call {CallId}.", call.CallId);
            await signaling.SendOfferAsync(offer.sdp);

            var answerTask = signaling.WaitForAnswerAsync(ct);
            var ringTimeoutTask = ringTimeoutSeconds is int seconds
                ? Task.Delay(TimeSpan.FromSeconds(seconds), ct)
                : null;
            logger.Information(
                "Waiting for WebRTC SDP answer over Nostr for call {CallId}{RingTimeout}.",
                call.CallId,
                ringTimeoutSeconds is int s ? $" (up to {s}s before declining)" : string.Empty);

            // calleeHangup is in here even without a ring timeout: a
            // callee who declines or hangs up while their device is
            // ringing is done with this call, and waiting out a timeout
            // that may never come just leaves the caller ringing.
            var waitTasks = ringTimeoutTask is not null
                ? new Task[] { answerTask, calleeHangup, ringTimeoutTask, call.WhenRemoteHungUp }
                : new Task[] { answerTask, calleeHangup, call.WhenRemoteHungUp };
            await Task.WhenAny(waitTasks);

            // Checked before call.WhenRemoteHungUp - see docs/voicemail.md
            // for why task-completion order alone isn't a reliable signal
            // here.
            if (ct.IsCancellationRequested)
            {
                logger.Information(
                    "Stopped waiting for a WebRTC SDP answer for call {CallId} because shutdown was requested.",
                    call.CallId);
                StopBridging();

                // Same reasoning as the two decision paths below: without
                // this, a restart leaves NosCall believing a call it was
                // never told about is still ringing. Best-effort like the
                // others - what makes this land isn't the relay
                // connection (still open here regardless) but whether the
                // publish gets to finish before the process exits, which
                // is CallHub.DrainAsync's job, not this method's.
                if (!calleeHangup.IsCompleted)
                {
                    await SendHangupSafeAsync(signaling, call.CallId, "sip2nostr shutting down");
                }

                pc.close();
                return true;
            }

            if (call.WhenRemoteHungUp.IsCompleted)
            {
                logger.Information("Call {CallId} ended before a WebRTC SDP answer arrived.", call.CallId);
                StopBridging();

                // Only when the callee hasn't already ended it their own
                // way - otherwise this is the mirror of the ring-timeout
                // case below: without it, NosCall is left believing the
                // call is still ringing (nothing else ever tells it
                // otherwise), so the next call in can find NosCall already
                // "busy" with an abandoned one.
                if (!calleeHangup.IsCompleted)
                {
                    // Covers both a caller CANCEL/BYE and sipsorcery's own
                    // MAX_RING_TIME expiry - Call.WhenRemoteHungUp doesn't
                    // distinguish them, and unlike the former, the latter
                    // doesn't actually mean the caller hung up.
                    await SendHangupSafeAsync(signaling, call.CallId, "call ended before it could be answered");
                }

                pc.close();
                return true;
            }

            if (!answerTask.IsCompleted)
            {
                if (calleeHangup.IsCompleted)
                {
                    logger.Information(
                        "Nostr side ended call {CallId} before answering it ({Reason}); declining.",
                        call.CallId,
                        DescribeReason(await calleeHangup));
                }
                else
                {
                    logger.Information(
                        "No WebRTC SDP answer arrived within {RingTimeoutSeconds}s for call {CallId}; declining.",
                        ringTimeoutSeconds,
                        call.CallId);
                    // Only when we're the one giving up - a device that
                    // just hung up doesn't need to be told to stop ringing.
                    await SendHangupSafeAsync(signaling, call.CallId, "no answer within ring timeout");
                }

                StopBridging();
                pc.close();
                return false;
            }

            var answerSdp = await answerTask;
            logger.Information("Received WebRTC SDP answer over Nostr for call {CallId}.", call.CallId);
            pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp });
        }
        catch (OperationCanceledException)
        {
            logger.Warning(
                "Stopped waiting for WebRTC SDP answer for call {CallId} because shutdown was requested.",
                call.CallId);
            StopBridging();
            pc.close();
            return true;
        }
        catch (Exception exception)
        {
            logger.Warning(
                exception,
                "Nostr signaling failed for call {CallId} before a WebRTC SDP answer arrived; declining.",
                call.CallId);
            StopBridging();
            pc.close();
            return false;
        }

        // Answered here, not before the offer went out: the caller hears
        // ringback for as long as the Nostr side is ringing, and a call
        // this sink ends up declining is still unanswered when
        // VoicemailSink gets it. Deliberately outside the try above - a
        // failure to answer a call the callee has already picked up is not
        // something the next sink should get a turn at.
        if (!await call.AnswerAsync())
        {
            logger.Warning(
                "Call {CallId} could not be answered after the WebRTC SDP answer arrived; closing the WebRTC session.",
                call.CallId);
            StopBridging();
            await SendHangupSafeAsync(signaling, call.CallId, "sip leg could not be answered");
            pc.close();
            return true;
        }

        // Either leg can end a bridged call, and the SIP leg only learns
        // about a Nostr-side hangup from the signaling event - closing the
        // peer connection alone would leave the caller on a silent call.
        // CallHub's own finally hangs the SIP leg up once this returns.
        // connectionLossWatcher covers the third way this can end: the
        // callee's device is simply gone (no internet, force-quit) and
        // never gets to send a hangup at all - see
        // docs/propagating-to-nostr.md.
        using var connectionLossWatcher = new ConnectionLossWatcher(TimeSpan.FromSeconds(ConnectionLossGraceSeconds));
        pc.onconnectionstatechange += connectionLossWatcher.OnStateChange;

        await Task.WhenAny(call.WhenRemoteHungUp, calleeHangup, connectionLossWatcher.WhenConnectionLost);
        pc.onconnectionstatechange -= connectionLossWatcher.OnStateChange;

        if (calleeHangup.IsCompleted && !call.WhenRemoteHungUp.IsCompleted)
        {
            logger.Information(
                "Nostr side hung up call {CallId} ({Reason}); ending the SIP leg.",
                call.CallId,
                DescribeReason(await calleeHangup));
        }
        else if (call.WhenRemoteHungUp.IsCompleted)
        {
            logger.Information("Call {CallId} ended; closing WebRTC session.", call.CallId);

            // The caller ending a live, bridged call is the one hangup
            // direction that was still silent: every ringing-stage exit
            // notifies NosCall, but this one relied on it noticing the
            // peer connection close on its own - the same assumption
            // docs/propagating-to-nostr.md's blind spots declines to make
            // in the other direction. Guarded the same way as the rest.
            if (!calleeHangup.IsCompleted)
            {
                await SendHangupSafeAsync(signaling, call.CallId, "caller hung up");
            }
        }
        else
        {
            // Neither leg said anything - connectionLossWatcher is the
            // only one of the three that can have completed here. Still
            // worth a best-effort hangup: the WebRTC media path being
            // down doesn't mean the Nostr relay connection is too (a
            // TURN-reachability failure, say, wouldn't take a plain
            // WebSocket down with it).
            logger.Warning(
                "WebRTC connection for call {CallId} was lost mid-call with no hangup from either side; ending it.",
                call.CallId);
            await SendHangupSafeAsync(signaling, call.CallId, "connection lost");
        }

        StopBridging();
        pc.close();
        return true;
    }

    private static string DescribeReason(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason;

    private async Task SendHangupSafeAsync(NostrSignalingClient signaling, string callId, string reason)
    {
        try
        {
            // Logged before and after, not just before: a shutdown that
            // exits mid-publish leaves only the "attempting" line, which
            // is the honest state of things rather than a claim the send
            // succeeded.
            logger.Information("Attempting to send WebRTC call hangup over Nostr for call {CallId} so the ringing device stops.", callId);
            await signaling.SendHangupAsync(reason);
            logger.Information("Sent WebRTC call hangup over Nostr for call {CallId}.", callId);
        }
        catch (Exception exception)
        {
            logger.Warning(exception, "Failed to send WebRTC call hangup over Nostr for call {CallId}.", callId);
        }
    }

    private async Task SendIceCandidateSafeAsync(NostrSignalingClient signaling, RTCIceCandidate candidate)
    {
        try
        {
            logger.Information("Sending WebRTC ICE candidate over Nostr.");
            await signaling.SendIceCandidateAsync(new IceCandidatePayload(candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex));
        }
        catch (Exception exception)
        {
            logger.Warning(exception, "Failed to send WebRTC ICE candidate over Nostr.");
        }
    }

    private List<RTCIceServer> BuildIceServers()
    {
        var servers = new List<RTCIceServer>();
        servers.AddRange(webRtcConfig.StunServers.Select(RTCIceServer.Parse));
        if (!string.IsNullOrWhiteSpace(webRtcConfig.TurnServer))
        {
            servers.Add(RTCIceServer.Parse(webRtcConfig.TurnServer));
        }

        return servers;
    }
}
