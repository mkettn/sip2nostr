namespace Sip2Nostr.Hub;

// Paces PCM samples out over an ICallAudio at 20ms frames - the generic
// replacement for SIPSorcery's AudioExtrasSource, which is tied to
// RTPSession and can't play through the source/sink-agnostic audio
// contract. Used for voicemail greetings/tones and the local test-audio
// sink.
public static class PcmPlayback
{
    private const int FrameMillis = 20;

    // Cancellation is treated as "stop playback", not an error - callers
    // race this against a hangup signal and don't want a thrown
    // OperationCanceledException on the common "caller hung up" path.
    public static async Task PlayOnceAsync(ICallAudio audio, short[] samples, int sampleRate, CancellationToken ct)
    {
        var frameSize = sampleRate * FrameMillis / 1000;
        for (var offset = 0; offset < samples.Length; offset += frameSize)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var length = Math.Min(frameSize, samples.Length - offset);
            audio.Send(samples[offset..(offset + length)]);

            try
            {
                await Task.Delay(FrameMillis, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public static async Task PlayLoopAsync(ICallAudio audio, short[] samples, int sampleRate, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PlayOnceAsync(audio, samples, sampleRate, ct);
        }
    }

    public static short[] GenerateTone(double frequencyHz, double durationSeconds, int sampleRate, double amplitude = 0.2)
    {
        var sampleCount = (int)(durationSeconds * sampleRate);
        var samples = new short[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate));
        }

        return samples;
    }
}
