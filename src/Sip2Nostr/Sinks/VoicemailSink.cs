using Serilog;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Sip2Nostr.Config;
using Sip2Nostr.Hub;
using Sip2Nostr.Shared;
using Sip2Nostr.Sip;
using Sip2Nostr.Voicemail;

namespace Sip2Nostr.Sinks;

// Answering-machine fallback: plays a greeting (or a short tone if none is
// configured), records the caller, and enqueues it for delivery over
// Nostr. Always handles the call it's offered - see docs/voicemail.md for
// the full flow and the exactly-one-job guarantee. The hub only ever
// exchanges RTP frames (see RtpAudioFrame), so this sink decodes recorded
// audio against Call.AudioFormat itself, and plays the greeting/tone via
// SIPSorcery's own AudioExtrasSource wired into Call.Audio.SendEncodedSample
// rather than reimplementing RTP pacing/framing.
public sealed class VoicemailSink(
    VoicemailConfig voicemailConfig,
    VoicemailSender voicemailSender,
    bool deliveryRequiresPcm,
    string configDirectory,
    ILogger logger) : ICallSink
{
    public async Task<bool> TryHandleAsync(Call call, CancellationToken ct)
    {
        try
        {
            await RunVoicemailAsync(call, ct);
        }
        catch (Exception exception)
        {
            // See docs/voicemail.md - a failure here still needs to notify
            // target_npub, not just log.
            logger.Error(exception, "Voicemail recording failed for call {CallId}; sending a missed-call notice instead.", call.CallId);
            voicemailSender.Enqueue(new MissedCallNoticeJob(call.CallerNumber, call.CallId));
        }

        return true;
    }

    private async Task RunVoicemailAsync(Call call, CancellationToken ct)
    {
        var sampleRate = call.AudioFormat.ClockRate;
        var decoder = new AudioEncoder();
        var recordingLock = new object();
        var recordedSamples = new List<short>();
        var recordingActive = false;

        void OnAudioReceived(RtpAudioFrame frame)
        {
            lock (recordingLock)
            {
                if (recordingActive)
                {
                    recordedSamples.AddRange(decoder.DecodeAudio(frame.Payload, call.AudioFormat));
                }
            }
        }

        var greetingSource = new AudioExtrasSource(new AudioEncoder(), new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence });
        greetingSource.SetAudioSourceFormat(call.AudioFormat);
        greetingSource.OnAudioSourceEncodedSample += call.Audio.SendEncodedSample;

        call.Audio.OnAudioReceived += OnAudioReceived;
        try
        {
            await greetingSource.StartAudio();

            var greetingPath = string.IsNullOrWhiteSpace(voicemailConfig.GreetingSound)
                ? null
                : SoundFileResolver.Resolve(voicemailConfig.GreetingSound, configDirectory, logger);

            if (greetingPath is not null)
            {
                logger.Information("Playing voicemail greeting {GreetingPath} for call {CallId}.", greetingPath, call.CallId);
                using var greetingStream = File.OpenRead(greetingPath);
                var playTask = greetingSource.SendAudioFromStream(greetingStream, AudioSamplingRatesEnum.Rate8KHz);
                if (await Task.WhenAny(playTask, call.WhenRemoteHungUp) == call.WhenRemoteHungUp)
                {
                    greetingSource.CancelSendAudioFromStream();
                    logger.Information(
                        "Caller {CallerNumber} hung up during the voicemail greeting; nothing recorded.",
                        call.CallerNumber);
                    voicemailSender.Enqueue(new MissedCallNoticeJob(call.CallerNumber, call.CallId));
                    return;
                }
            }
            else
            {
                logger.Information("No voicemail greeting configured; playing a short tone before recording for call {CallId}.", call.CallId);
                greetingSource.SetSource(AudioSourcesEnum.SineWave);
                if (await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1.5), ct), call.WhenRemoteHungUp) == call.WhenRemoteHungUp)
                {
                    logger.Information(
                        "Caller {CallerNumber} hung up before the voicemail tone finished; nothing recorded.",
                        call.CallerNumber);
                    voicemailSender.Enqueue(new MissedCallNoticeJob(call.CallerNumber, call.CallId));
                    return;
                }

                greetingSource.SetSource(AudioSourcesEnum.Silence);
            }

            logger.Information("Recording voicemail for up to {MaxRecordingSeconds}s for call {CallId}.", voicemailConfig.MaxRecordingSeconds, call.CallId);
            lock (recordingLock)
            {
                recordingActive = true;
            }

            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(voicemailConfig.MaxRecordingSeconds), ct), call.WhenRemoteHungUp);

            lock (recordingLock)
            {
                recordingActive = false;
            }
        }
        finally
        {
            call.Audio.OnAudioReceived -= OnAudioReceived;
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
                call.CallerNumber,
                recordedSeconds);
            voicemailSender.Enqueue(new MissedCallNoticeJob(call.CallerNumber, call.CallId));
            return;
        }

        logger.Information(
            "Voicemail recording from {CallerNumber} finished: {RecordedSeconds:F1}s captured.",
            call.CallerNumber,
            recordedSeconds);
        var durationSeconds = (int)Math.Round(recordedSeconds);
        var opusPath = await SaveRecordingAsync(samples, sampleRate, call.CallId, call.CallerNumber);

        // Only a backend that actually reads VoicemailAudioJob.Samples
        // (TranscribedTextDeliveryBackend, for whisper.cpp) needs PCM
        // carried in the job; asking the backend itself (rather than
        // re-deriving the same answer from [voicemail].delivery here)
        // keeps this correct if a future backend's PCM needs don't line
        // up with today's two-mode delivery split.
        var jobSamples = deliveryRequiresPcm ? samples : [];
        voicemailSender.Enqueue(new VoicemailAudioJob(opusPath, jobSamples, sampleRate, durationSeconds, call.CallerNumber, call.CallId));
    }

    private async Task<string> SaveRecordingAsync(short[] samples, int sampleRate, string callId, string callerNumber)
    {
        var opusBytes = OpusCodec.Encode(samples, sampleRate, VoicemailBudget.OpusBitrateBps, voicemailConfig.OpusResamplerQuality);
        var recordingsDir = Path.IsPathRooted(voicemailConfig.RecordingsDir)
            ? voicemailConfig.RecordingsDir
            : Path.GetFullPath(Path.Combine(configDirectory, voicemailConfig.RecordingsDir));

        var opusPath = Path.GetFullPath(Path.Combine(recordingsDir, ResolveRecordingFilename(callId, callerNumber)));
        Directory.CreateDirectory(Path.GetDirectoryName(opusPath)!);
        await File.WriteAllBytesAsync(opusPath, opusBytes);
        logger.Information("Saved voicemail recording to {OpusPath}.", opusPath);
        return opusPath;
    }

    private string ResolveRecordingFilename(string callId, string callerNumber)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        return voicemailConfig.RecordingFilename
            .Replace("{timestamp}", timestamp, StringComparison.OrdinalIgnoreCase)
            .Replace("{caller}", callerNumber, StringComparison.OrdinalIgnoreCase)
            .Replace("{call_id}", callId, StringComparison.OrdinalIgnoreCase);
    }
}
