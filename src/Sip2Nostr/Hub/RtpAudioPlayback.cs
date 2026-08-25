using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace Sip2Nostr.Hub;

// Encodes PCM samples to the given codec and paces them out over an
// ICallAudio as RTP frames at 20ms intervals - the shared player for
// voicemail greetings/tones and the local test-audio sink, both of which
// only have PCM to play (a generated tone, or a sound file decoded via
// ffmpeg) but must hand it to the hub as RTP frames like everything else.
public static class RtpAudioPlayback
{
    private const int FrameMillis = 20;

    // Cancellation is treated as "stop playback", not an error - callers
    // race this against a hangup signal and don't want a thrown
    // OperationCanceledException on the common "caller hung up" path.
    public static async Task PlayOnceAsync(ICallAudio audio, short[] samples, AudioFormat audioFormat, CancellationToken ct)
    {
        var encoder = new AudioEncoder();
        var frameSize = audioFormat.ClockRate * FrameMillis / 1000;
        var timestamp = 0u;
        for (var offset = 0; offset < samples.Length; offset += frameSize)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var length = Math.Min(frameSize, samples.Length - offset);
            var frameSamples = samples[offset..(offset + length)];
            var payload = encoder.EncodeAudio(frameSamples, audioFormat);
            audio.Send(new RtpAudioFrame(payload, timestamp, MarkerBit: 0, audioFormat.FormatID));
            timestamp += (uint)length;

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

    public static async Task PlayLoopAsync(ICallAudio audio, short[] samples, AudioFormat audioFormat, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PlayOnceAsync(audio, samples, audioFormat, ct);
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
