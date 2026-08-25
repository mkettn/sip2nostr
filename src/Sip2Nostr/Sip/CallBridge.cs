using System.Net;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Sip2Nostr.CallerList;
using Sip2Nostr.Config;
using Sip2Nostr.Signaling;
using Sip2Nostr.Voicemail;

namespace Sip2Nostr.Sip;

// Bridges one inbound call: answers the SIP/RTP leg (sipsorcery RTPSession)
// and a WebRTC leg (sipsorcery RTCPeerConnection) using the same G.711
// (PCMU/PCMA) codec on both sides, forwarding raw RTP packets between them
// so no transcoding is needed. WebRTC offer/answer/ICE is carried over
// Nostr via NostrSignalingClient.
public sealed class CallBridge(
    WebRtcConfig webRtcConfig,
    NostrConfig nostrConfig,
    VoicemailConfig voicemailConfig,
    VoicemailSender voicemailSender,
    string configDirectory,
    IPAddress localMediaAddress,
    int rtpPort,
    CallerListGate callerListGate,
    ILogger logger)
{
    private static readonly SDPWellKnownMediaFormatsEnum[] PreferredAudioFormats =
    [
        SDPWellKnownMediaFormatsEnum.PCMA,
        SDPWellKnownMediaFormatsEnum.PCMU,
    ];

    public async Task HandleIncomingCallAsync(
        SIPUserAgent ua,
        SIPRequest inviteRequest,
        LineConfig? matchedLine,
        CancellationToken ct)
    {
        logger.Information("Accepting SIP call for {RequestUri}.", inviteRequest.URI);
        LogInviteSdp(inviteRequest);
        var selectedAudioFormat = SelectOfferedG711Format(inviteRequest);
        logger.Information("Selected SIP audio codec {AudioCodec} for the SDP answer.", selectedAudioFormat);
        var uas = ua.AcceptCall(inviteRequest);
        uas.ClientTransaction.OnAckReceived += (_, _, _, ackRequest) =>
        {
            logger.Information(
                "Received SIP ACK for answered INVITE; call-id: {CallId}; request URI: {RequestUri}; to tag: {ToTag}; from tag: {FromTag}.",
                ackRequest.Header.CallId,
                ackRequest.URI,
                ackRequest.Header.To?.ToTag,
                ackRequest.Header.From?.FromTag);
            return Task.FromResult(System.Net.Sockets.SocketError.Success);
        };
        logger.Information("Accepted SIP INVITE with local transaction tag {LocalTag}.", uas.ClientTransaction.LocalTag);

        var rawCallerNumber = inviteRequest.Header.From?.FromURI?.User ?? string.Empty;
        var callerNumber = PhoneNumberNormalizer.Normalize(rawCallerNumber);
        logger.Information(
            "Caller number normalized to {CallerNumber} (raw: {RawCallerNumber}).",
            callerNumber,
            rawCallerNumber);

        if (!await callerListGate.IsAllowedAsync(callerNumber, ct))
        {
            logger.Information("Caller {CallerNumber} is not allowed to reach this line; rejecting.", callerNumber);
            uas.Reject(SIPResponseStatusCodesEnum.Forbidden, null);
            return;
        }

        if (!nostrConfig.Enabled)
        {
            await AnswerWithLocalAudioAsync(ua, uas, matchedLine, ct);
            return;
        }

        var sipMediaSession = new RTPSession(false, false, false);
        sipMediaSession.addTrack(CreateAudioTrack(selectedAudioFormat, MediaStreamStatusEnum.SendRecv));

        var rtcConfig = new RTCConfiguration { iceServers = BuildIceServers() };
        var pc = new RTCPeerConnection(rtcConfig, 0, null, false);
        pc.addTrack(CreateAudioTrack(selectedAudioFormat, MediaStreamStatusEnum.SendRecv));
        logger.Information(
            "Created WebRTC peer connection with {IceServerCount} ICE server(s).",
            rtcConfig.iceServers.Count);

        // Gates the SIP-leg -> WebRTC-leg forwarding direction only; it's
        // switched off if the call falls back to voicemail below, so a late
        // caller-side RTP packet never gets handed to a closed peer
        // connection. The other direction needs no gate: it naturally stops
        // once pc.close() below stops firing its OnRtpPacketReceived event.
        // Volatile: the write happens on this async method's thread, the
        // read happens on SIPSorcery's RTP receive thread.
        var forwardToWebRtc = true;
        BridgeAudio(sipMediaSession, pc, () => Volatile.Read(ref forwardToWebRtc));

        logger.Information("Answering SIP call.");
        var answered = await ua.Answer(uas, sipMediaSession, null, localMediaAddress);
        LogFinalInviteResponse(uas);
        if (!answered)
        {
            logger.Warning("SIP call answer failed; closing media sessions.");
            sipMediaSession.Close("sip answer failed");
            pc.close();
            return;
        }

        var callId = Guid.NewGuid().ToString();
        logger.Information("SIP call answered; connecting Nostr signaling with call-id {CallId}.", callId);
        await using var signaling = new NostrSignalingClient(nostrConfig, callId, logger.ForContext<NostrSignalingClient>());

        // Wired up before waiting on the Nostr answer (not after) so a
        // caller hangup while we're still waiting for NosCall to answer
        // tears the call down promptly instead of leaking until shutdown.
        var hangupTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ua.OnCallHungup += _ => hangupTcs.TrySetResult();
        using var ctReg = ct.Register(() => hangupTcs.TrySetResult());

        var nostrAnswered = false;
        try
        {
            await signaling.ConnectAsync();
            logger.Information("Nostr signaling connected.");

            pc.onicecandidate += candidate =>
            {
                _ = SendIceCandidateSafeAsync(signaling, candidate);
            };
            signaling.OnIceCandidateReceived(candidate =>
            {
                logger.Information("Received WebRTC ICE candidate over Nostr.");
                pc.addIceCandidate(new RTCIceCandidateInit
                {
                    candidate = candidate.Candidate,
                    sdpMid = candidate.SdpMid,
                    sdpMLineIndex = candidate.SdpMLineIndex,
                });
            });

            var offer = pc.createOffer(null);
            await pc.setLocalDescription(offer);
            logger.Information("Sending WebRTC SDP offer over Nostr.");
            await signaling.SendOfferAsync(offer.sdp);

            var answerTask = signaling.WaitForAnswerAsync(ct);
            var ringTimeoutTask = voicemailConfig.Enabled
                ? Task.Delay(TimeSpan.FromSeconds(voicemailConfig.RingTimeoutSeconds), ct)
                : null;
            logger.Information(
                "Waiting for WebRTC SDP answer over Nostr{RingTimeout}.",
                voicemailConfig.Enabled ? $" (up to {voicemailConfig.RingTimeoutSeconds}s before falling back to voicemail)" : string.Empty);

            var waitTasks = ringTimeoutTask is not null
                ? new Task[] { answerTask, ringTimeoutTask, hangupTcs.Task }
                : new Task[] { answerTask, hangupTcs.Task };
            await Task.WhenAny(waitTasks);

            // hangupTcs also completes on shutdown (ctReg above). Checking
            // it's completed - rather than trusting which task Task.WhenAny
            // happened to report as the winner - avoids a race: cancellation
            // callbacks on a token run LIFO, and ringTimeoutTask's own
            // internal registration on ct is created after ctReg above, so
            // on shutdown it can observe cancellation (and so complete)
            // before ctReg's callback runs, which would otherwise misreport
            // a shutdown as a genuine ring timeout.
            if (hangupTcs.Task.IsCompleted)
            {
                logger.Information("Call ended before a WebRTC SDP answer arrived; closing media sessions.");
                pc.close();
                ua.Hangup();
                return;
            }

            // Checking answerTask itself, rather than ringTimeoutTask, gives
            // a genuine answer priority if it lands at the same moment the
            // ring timeout elapses - Task.WhenAny only guarantees *a* task
            // completed, not which one "should" win a near-simultaneous
            // race. If answerTask hasn't completed here, hangupTcs hasn't
            // either (checked above, and it shares ct with answerTask's own
            // cancellation registration), so ringTimeoutTask is the only
            // remaining task that could have completed.
            if (answerTask.IsCompleted)
            {
                var answerSdp = await answerTask;
                logger.Information("Received WebRTC SDP answer over Nostr.");
                pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp });
                nostrAnswered = true;
            }
            else
            {
                logger.Information(
                    "No WebRTC SDP answer arrived within {RingTimeoutSeconds}s; falling back to voicemail.",
                    voicemailConfig.RingTimeoutSeconds);
            }
        }
        catch (OperationCanceledException)
        {
            logger.Warning("Stopped waiting for WebRTC SDP answer because shutdown was requested.");
            pc.close();
            ua.Hangup();
            return;
        }
        catch (Exception exception)
        {
            logger.Warning(exception, "Nostr signaling failed before a WebRTC SDP answer arrived; falling back to voicemail.");
        }

        if (nostrAnswered)
        {
            await hangupTcs.Task;
            logger.Information("Call ended; closing media sessions.");
            pc.close();
            // ua.Hangup() rather than a plain Close(): hangupTcs also
            // completes on shutdown, and at that point the call may still
            // be actively bridged (unlike the caller-hung-up case, where
            // this is already a safe no-op per IsCallActive) - so this is
            // what actually sends a BYE in that case, matching every other
            // path in this method.
            ua.Hangup();
            return;
        }

        // No WebRTC answer, by timeout or by a signaling failure - stop
        // trying to bridge to the (now closed) peer connection and either
        // record a voicemail or just leave the call connected to silence.
        Volatile.Write(ref forwardToWebRtc, false);
        pc.close();
        await SendHangupSafeAsync(signaling);

        if (!voicemailConfig.Enabled)
        {
            logger.Information("Voicemail is disabled; leaving the call connected with silence until the caller hangs up.");
            await hangupTcs.Task;
            ua.Hangup();
            return;
        }

        try
        {
            await RunVoicemailAsync(sipMediaSession, selectedAudioFormat, callerNumber, callId, hangupTcs, ct);
        }
        catch (Exception exception)
        {
            // RunVoicemailAsync only enqueues a job on its own success
            // paths (the early returns and the final recording-finished
            // path each enqueue exactly one) - if it throws instead (a
            // bad greeting_sound path, a disk error saving the
            // recording), none of those run, and nothing gets enqueued
            // at all. That would silently drop exactly the caller this
            // notice feature exists for, so send one here instead of
            // letting the failure propagate to the generic per-call
            // catch in BridgeService, which only logs.
            logger.Error(exception, "Voicemail recording failed for call {CallId}; sending a missed-call notice instead.", callId);
            voicemailSender.Enqueue(new MissedCallNoticeJob(callerNumber, callId));
        }
        finally
        {
            // Ends the SIP dialog (BYE) and closes the media session -
            // ua.Hangup() no-ops safely if the caller already hung up
            // during the greeting/recording (SIPUserAgent.Hangup() checks
            // IsCallActive). Always runs, even if RunVoicemailAsync threw
            // (e.g. a bad greeting_sound path, or a disk error saving the
            // recording), so a failure there can't leak the RTP session -
            // and the caller, whose max_recording_seconds elapsed, is
            // actually disconnected instead of left on a silent call.
            ua.Hangup();
        }
    }

    // The bridge originated this call (it's the NIP-AC caller), so giving
    // up on it is a Hangup, not a Reject - see NostrSignalingClient.
    private async Task SendHangupSafeAsync(NostrSignalingClient signaling)
    {
        try
        {
            logger.Information("Sending WebRTC call hangup over Nostr so the ringing device stops.");
            await signaling.SendHangupAsync("no answer - falling back to voicemail");
        }
        catch (Exception exception)
        {
            logger.Warning(exception, "Failed to send WebRTC call hangup over Nostr.");
        }
    }

    // Plays the configured greeting once (or a short tone if none is
    // configured), then records the caller's audio - captured from the
    // already-answered SIP RTP session directly, the same way BridgeAudio
    // relays it when a WebRTC leg is present - for up to
    // [voicemail].max_recording_seconds or until the caller hangs up,
    // whichever comes first. See docs/voicemail.md.
    //
    // Exactly one job is ever enqueued on voicemailSender per call: a
    // MissedCallNoticeJob if nothing worth sending was recorded (caller
    // hung up during the greeting/tone, or the recording was too short),
    // otherwise a VoicemailAudioJob - never both, so target_npub gets a
    // single DM per missed call either way.
    private async Task RunVoicemailAsync(
        RTPSession sipMediaSession,
        SDPWellKnownMediaFormatsEnum selectedAudioFormat,
        string callerNumber,
        string callId,
        TaskCompletionSource hangupTcs,
        CancellationToken ct)
    {
        var audioFormat = new AudioFormat(selectedAudioFormat);
        var sampleRate = audioFormat.ClockRate;
        var decoder = new AudioEncoder();
        var recordingLock = new object();
        var recordedSamples = new List<short>();
        var recordingActive = false;

        // recordingActive is read and written under recordingLock on both
        // sides - it's read on SIPSorcery's RTP receive thread and written
        // on this async method's thread, so without a shared lock there's
        // no guarantee the receive thread ever observes the write.
        sipMediaSession.OnRtpPacketReceived += (_, media, pkt) =>
        {
            if (media != SDPMediaTypesEnum.audio)
            {
                return;
            }

            lock (recordingLock)
            {
                if (!recordingActive)
                {
                    return;
                }

                recordedSamples.AddRange(decoder.DecodeAudio(pkt.Payload, audioFormat));
            }
        };

        var greetingSource = new AudioExtrasSource(new AudioEncoder(), new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence });
        greetingSource.SetAudioSourceFormat(audioFormat);
        greetingSource.OnAudioSourceEncodedSample += sipMediaSession.SendAudio;

        try
        {
            await greetingSource.StartAudio();

            var greetingPath = string.IsNullOrWhiteSpace(voicemailConfig.GreetingSound)
                ? null
                : ResolveSoundPath(voicemailConfig.GreetingSound);

            if (greetingPath is not null)
            {
                logger.Information("Playing voicemail greeting {GreetingPath}.", greetingPath);
                using var greetingStream = File.OpenRead(greetingPath);
                var playTask = greetingSource.SendAudioFromStream(greetingStream, AudioSamplingRatesEnum.Rate8KHz);
                if (await Task.WhenAny(playTask, hangupTcs.Task) == hangupTcs.Task)
                {
                    greetingSource.CancelSendAudioFromStream();
                    logger.Information(
                        "Caller {CallerNumber} hung up during the voicemail greeting; nothing recorded.",
                        callerNumber);
                    voicemailSender.Enqueue(new MissedCallNoticeJob(callerNumber, callId));
                    return;
                }
            }
            else
            {
                logger.Information("No voicemail greeting configured; playing a short tone before recording.");
                greetingSource.SetSource(AudioSourcesEnum.SineWave);
                if (await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1.5), ct), hangupTcs.Task) == hangupTcs.Task)
                {
                    logger.Information(
                        "Caller {CallerNumber} hung up before the voicemail tone finished; nothing recorded.",
                        callerNumber);
                    voicemailSender.Enqueue(new MissedCallNoticeJob(callerNumber, callId));
                    return;
                }

                greetingSource.SetSource(AudioSourcesEnum.Silence);
            }

            logger.Information("Recording voicemail for up to {MaxRecordingSeconds}s.", voicemailConfig.MaxRecordingSeconds);
            lock (recordingLock)
            {
                recordingActive = true;
            }

            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(voicemailConfig.MaxRecordingSeconds), ct), hangupTcs.Task);

            lock (recordingLock)
            {
                recordingActive = false;
            }
        }
        finally
        {
            await greetingSource.CloseAudio();
        }

        short[] samples;
        lock (recordingLock)
        {
            samples = recordedSamples.ToArray();
        }

        var recordedSeconds = samples.Length / (double)sampleRate;
        if (recordedSeconds < 1.0)
        {
            logger.Information(
                "Voicemail recording from {CallerNumber} was too short ({RecordedSeconds:F1}s); not sending.",
                callerNumber,
                recordedSeconds);
            voicemailSender.Enqueue(new MissedCallNoticeJob(callerNumber, callId));
            return;
        }

        logger.Information(
            "Voicemail recording from {CallerNumber} finished: {RecordedSeconds:F1}s captured.",
            callerNumber,
            recordedSeconds);
        var durationSeconds = (int)Math.Round(recordedSeconds);
        var wavPath = await SaveRecordingAsync(samples, sampleRate, callId);
        voicemailSender.Enqueue(new VoicemailAudioJob(wavPath, sampleRate, durationSeconds, callerNumber, callId));
    }

    // Just the durable local write - encoding and sending happen later,
    // off this call's critical path, in VoicemailSender. Always saved
    // regardless of what happens after, so a delivery failure never loses
    // the recording.
    private async Task<string> SaveRecordingAsync(short[] samples, int sampleRate, string callId)
    {
        var wavBytes = WavEncoder.Encode(samples, sampleRate);
        var recordingsDir = Path.IsPathRooted(voicemailConfig.RecordingsDir)
            ? voicemailConfig.RecordingsDir
            : Path.GetFullPath(Path.Combine(configDirectory, voicemailConfig.RecordingsDir));
        Directory.CreateDirectory(recordingsDir);

        var wavPath = Path.Combine(recordingsDir, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{callId}.wav");
        await File.WriteAllBytesAsync(wavPath, wavBytes);
        logger.Information("Saved voicemail recording to {WavPath}.", wavPath);
        return wavPath;
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

    // forwardSipToWebRtc gates only the SIP-leg -> WebRTC-leg direction, so
    // a caller falling back to voicemail can be switched off this relay
    // without also having to unhook the lambda below (event handlers can't
    // be removed once subscribed as an anonymous delegate).
    private static void BridgeAudio(RTPSession sipSide, RTPSession webRtcSide, Func<bool> forwardSipToWebRtc)
    {
        sipSide.OnRtpPacketReceived += (_, media, pkt) =>
        {
            if (media == SDPMediaTypesEnum.audio && forwardSipToWebRtc())
            {
                webRtcSide.SendRtpRaw(media, pkt.Payload, pkt.Header.Timestamp, pkt.Header.MarkerBit, pkt.Header.PayloadType);
            }
        };
        webRtcSide.OnRtpPacketReceived += (_, media, pkt) =>
        {
            if (media == SDPMediaTypesEnum.audio)
            {
                sipSide.SendRtpRaw(media, pkt.Payload, pkt.Header.Timestamp, pkt.Header.MarkerBit, pkt.Header.PayloadType);
            }
        };
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

    private async Task AnswerWithLocalAudioAsync(
        SIPUserAgent ua,
        SIPServerUserAgent uas,
        LineConfig? matchedLine,
        CancellationToken ct)
    {
        logger.Information("Nostr signaling is disabled; answering SIP call with local test audio only.");

        var mediaSession = new AudioSendOnlyMediaSession(localMediaAddress, rtpPort);
        var selectedAudioFormat = SelectOfferedG711Format(uas.CallRequest);
        RestrictAudioTrack(mediaSession.AudioLocalTrack, selectedAudioFormat);
        ConfigureTestAudioSource(mediaSession, matchedLine);

        logger.Information("Answering SIP call.");
        var answered = await ua.Answer(uas, mediaSession, null, localMediaAddress);
        LogFinalInviteResponse(uas);
        if (!answered)
        {
            logger.Warning("SIP call answer failed; closing local audio session.");
            mediaSession.Close("sip answer failed");
            return;
        }

        logger.Information("SIP call answered; local audio source started.");

        var hangupTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ua.OnCallHungup += _ => hangupTcs.TrySetResult();
        using var ctReg = ct.Register(() => hangupTcs.TrySetResult());
        await hangupTcs.Task;

        logger.Information("Call ended; closing local audio session.");
        mediaSession.Close("call ended");
    }

    private void ConfigureTestAudioSource(AudioSendOnlyMediaSession mediaSession, LineConfig? matchedLine)
    {
        if (!string.IsNullOrWhiteSpace(matchedLine?.Sound))
        {
            var soundPath = ResolveSoundPath(matchedLine.Sound);
            if (soundPath is not null)
            {
                logger.Information("Playing local test sound {SoundPath} on loop for line {LineLabel}.", soundPath, matchedLine.Label);
                mediaSession.AudioExtrasSource.SetSource(new AudioSourceOptions
                {
                    AudioSource = AudioSourcesEnum.Music,
                    MusicFile = soundPath,
                    MusicInputSamplingRate = AudioSamplingRatesEnum.Rate8KHz,
                });
                return;
            }

            logger.Warning(
                "Configured sound file {SoundPath} for line {LineLabel} could not be used; sending sine wave instead.",
                matchedLine.Sound,
                matchedLine.Label);
        }
        else
        {
            logger.Information("No line sound configured; sending sine wave test audio.");
        }

        mediaSession.AudioExtrasSource.SetSource(AudioSourcesEnum.SineWave);
    }

    private static MediaStreamTrack CreateAudioTrack(
        SDPWellKnownMediaFormatsEnum audioFormat,
        MediaStreamStatusEnum streamStatus) =>
        new(new AudioFormat(audioFormat), streamStatus);

    private void RestrictAudioTrack(MediaStreamTrack? track, SDPWellKnownMediaFormatsEnum audioFormat)
    {
        if (track is null)
        {
            logger.Warning("Could not restrict local audio track to {AudioCodec}; no local track was available.", audioFormat);
            return;
        }

        track.NoDtmfSupport = true;
        if (!track.RestrictCapabilities(new AudioFormat(audioFormat)))
        {
            logger.Warning("Could not restrict local audio track to {AudioCodec}; using SIPSorcery defaults.", audioFormat);
        }
    }

    private static SDPWellKnownMediaFormatsEnum SelectOfferedG711Format(SIPRequest inviteRequest)
    {
        var offeredPayloads = GetOfferedAudioPayloads(inviteRequest.Body);
        foreach (var payload in offeredPayloads)
        {
            if (payload == "8")
            {
                return SDPWellKnownMediaFormatsEnum.PCMA;
            }

            if (payload == "0")
            {
                return SDPWellKnownMediaFormatsEnum.PCMU;
            }
        }

        return PreferredAudioFormats[0];
    }

    private static IEnumerable<string> GetOfferedAudioPayloads(string? sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp))
        {
            yield break;
        }

        foreach (var line in sdp.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("m=audio ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var index = 3; index < parts.Length; index++)
            {
                yield return parts[index];
            }

            yield break;
        }
    }

    private void LogFinalInviteResponse(SIPServerUserAgent uas)
    {
        var response = uas.ClientTransaction.TransactionFinalResponse;
        if (response is null)
        {
            logger.Warning("SIP INVITE transaction has no final response to log after answer attempt.");
            return;
        }

        logger.Information("Actual final SIP INVITE response:\n{SipResponse}", response.ToString().Trim());
    }

    private string? ResolveSoundPath(string soundPath)
    {
        var resolvedSoundPath = Path.IsPathRooted(soundPath)
            ? soundPath
            : Path.GetFullPath(Path.Combine(configDirectory, soundPath));

        if (!File.Exists(resolvedSoundPath))
        {
            logger.Warning(
                "Configured sound file {SoundPath} resolved to {ResolvedSoundPath}, but it does not exist.",
                soundPath,
                resolvedSoundPath);
            return null;
        }

        if (IsRawPcmPath(resolvedSoundPath))
        {
            return resolvedSoundPath;
        }

        return ConvertSoundToRawPcm(resolvedSoundPath);
    }

    private string? ConvertSoundToRawPcm(string soundPath)
    {
        var cachePath = GetConvertedSoundPath(soundPath);
        if (File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(soundPath))
        {
            return cachePath;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList =
                {
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-y",
                    "-i",
                    soundPath,
                    "-ac",
                    "1",
                    "-ar",
                    "8000",
                    "-f",
                    "s16le",
                    cachePath,
                },
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                logger.Warning("Could not start ffmpeg to convert {SoundPath}.", soundPath);
                return null;
            }

            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                logger.Warning(
                    "ffmpeg failed to convert {SoundPath} to raw PCM: {FfmpegError}",
                    soundPath,
                    error.Trim());
                return null;
            }

            logger.Information("Converted {SoundPath} to raw 8 kHz PCM at {ConvertedSoundPath}.", soundPath, cachePath);
            return cachePath;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.Warning(exception, "Could not convert {SoundPath}; install ffmpeg or provide raw 8 kHz 16-bit PCM.", soundPath);
            return null;
        }
    }

    private static bool IsRawPcmPath(string soundPath)
    {
        var extension = Path.GetExtension(soundPath);
        return extension.Equals(".pcm", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".raw", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".s16le", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetConvertedSoundPath(string soundPath)
    {
        var fullPath = Path.GetFullPath(soundPath);
        var lastWriteTime = File.GetLastWriteTimeUtc(fullPath).Ticks;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{fullPath}:{lastWriteTime}")))[..16];
        return Path.Combine(Path.GetTempPath(), "sip2nostr", "sounds", $"{hash}.s16le");
    }

    private void LogInviteSdp(SIPRequest inviteRequest)
    {
        if (string.IsNullOrWhiteSpace(inviteRequest.Body))
        {
            logger.Warning("Incoming INVITE has no SDP body.");
            return;
        }

        logger.Information("Incoming INVITE SDP offer:\n{SdpOffer}", inviteRequest.Body.Trim());
    }
}
