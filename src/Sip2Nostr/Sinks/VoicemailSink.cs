using Serilog;
using Sip2Nostr.Config;
using Sip2Nostr.Hub;
using Sip2Nostr.Shared;
using Sip2Nostr.Sip;
using Sip2Nostr.Voicemail;

namespace Sip2Nostr.Sinks;

// Answering-machine fallback: plays a greeting (or a short tone if none is
// configured), records the caller, and enqueues it for delivery over
// Nostr. Always handles the call it's offered - see docs/voicemail.md for
// the full flow and the exactly-one-job guarantee.
public sealed class VoicemailSink(
    VoicemailConfig voicemailConfig,
    VoicemailSender voicemailSender,
    string configDirectory,
    ILogger logger) : ICallSink
{
    private const int SampleRate = 8000;

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
        var recordingLock = new object();
        var recordedSamples = new List<short>();
        var recordingActive = false;

        void OnAudioReceived(short[] samples)
        {
            lock (recordingLock)
            {
                if (recordingActive)
                {
                    recordedSamples.AddRange(samples);
                }
            }
        }

        call.Audio.OnAudioReceived += OnAudioReceived;
        try
        {
            var greetingPath = string.IsNullOrWhiteSpace(voicemailConfig.GreetingSound)
                ? null
                : SoundFileResolver.Resolve(voicemailConfig.GreetingSound, configDirectory, logger);

            if (greetingPath is not null)
            {
                logger.Information("Playing voicemail greeting {GreetingPath} for call {CallId}.", greetingPath, call.CallId);
                var greetingSamples = SoundFileResolver.LoadPcmSamples(greetingPath);
                if (await PlayUntilHungUpAsync(call, greetingSamples, ct))
                {
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
                var tone = PcmPlayback.GenerateTone(440, 1.5, SampleRate);
                if (await PlayUntilHungUpAsync(call, tone, ct))
                {
                    logger.Information(
                        "Caller {CallerNumber} hung up before the voicemail tone finished; nothing recorded.",
                        call.CallerNumber);
                    voicemailSender.Enqueue(new MissedCallNoticeJob(call.CallerNumber, call.CallId));
                    return;
                }
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
        }

        short[] samples;
        lock (recordingLock)
        {
            samples = recordedSamples.ToArray();
        }

        var recordedSeconds = samples.Length / (double)SampleRate;
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
        var wavPath = await SaveRecordingAsync(samples, call.CallId);
        voicemailSender.Enqueue(new VoicemailAudioJob(wavPath, SampleRate, durationSeconds, call.CallerNumber, call.CallId));
    }

    // Returns true if the caller hung up before playback finished.
    private static async Task<bool> PlayUntilHungUpAsync(Call call, short[] samples, CancellationToken ct)
    {
        var playTask = PcmPlayback.PlayOnceAsync(call.Audio, samples, SampleRate, ct);
        return await Task.WhenAny(playTask, call.WhenRemoteHungUp) == call.WhenRemoteHungUp;
    }

    private async Task<string> SaveRecordingAsync(short[] samples, string callId)
    {
        var wavBytes = WavEncoder.Encode(samples, SampleRate);
        var recordingsDir = Path.IsPathRooted(voicemailConfig.RecordingsDir)
            ? voicemailConfig.RecordingsDir
            : Path.GetFullPath(Path.Combine(configDirectory, voicemailConfig.RecordingsDir));
        Directory.CreateDirectory(recordingsDir);

        var wavPath = Path.Combine(recordingsDir, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{callId}.wav");
        await File.WriteAllBytesAsync(wavPath, wavBytes);
        logger.Information("Saved voicemail recording to {WavPath}.", wavPath);
        return wavPath;
    }
}
