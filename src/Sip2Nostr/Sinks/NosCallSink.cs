using Serilog;
using SIPSorcery.Net;
using Sip2Nostr.Config;
using Sip2Nostr.Hub;
using Sip2Nostr.Signaling;
using Sip2Nostr.Sip;

namespace Sip2Nostr.Sinks;

// Rings the callee over Nostr/WebRTC signaling (NIP-17 DMs carrying SDP
// offer/answer and ICE candidates - see NostrSignalingClient). If
// ringTimeoutSeconds is set and nobody answers within it, declines so the
// next sink (typically VoicemailSink) gets a turn; if unset, rings until
// the caller hangs up.
public sealed class NosCallSink(
    NostrConfig nostrConfig,
    WebRtcConfig webRtcConfig,
    int? ringTimeoutSeconds,
    ILogger logger) : ICallSink
{
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

            var waitTasks = ringTimeoutTask is not null
                ? new Task[] { answerTask, ringTimeoutTask, call.WhenRemoteHungUp }
                : new Task[] { answerTask, call.WhenRemoteHungUp };
            await Task.WhenAny(waitTasks);

            if (call.WhenRemoteHungUp.IsCompleted)
            {
                logger.Information("Call {CallId} ended before a WebRTC SDP answer arrived.", call.CallId);
                StopBridging();
                pc.close();
                return true;
            }

            if (!answerTask.IsCompleted)
            {
                logger.Information(
                    "No WebRTC SDP answer arrived within {RingTimeoutSeconds}s for call {CallId}; declining.",
                    ringTimeoutSeconds,
                    call.CallId);
                StopBridging();
                await SendHangupSafeAsync(signaling, call.CallId);
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

        await call.WhenRemoteHungUp;
        logger.Information("Call {CallId} ended; closing WebRTC session.", call.CallId);
        StopBridging();
        pc.close();
        return true;
    }

    private async Task SendHangupSafeAsync(NostrSignalingClient signaling, string callId)
    {
        try
        {
            logger.Information("Sending WebRTC call hangup over Nostr for call {CallId} so the ringing device stops.", callId);
            await signaling.SendHangupAsync("no answer within ring timeout");
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
