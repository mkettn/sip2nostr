using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using Nostr.Sdk;
using Serilog;
using Sip2Nostr.Shared;
using Sip2Nostr.Sip;

namespace Sip2Nostr.Voicemail;

// Default voicemail delivery backend: encodes the recording to Opus/OGG
// and inlines it as a base64 data: URI in the DM content. See
// docs/voicemail.md for the NIP-17 size budget this fits inside.
public sealed class AudioInlineDeliveryBackend(ILogger logger) : IVoicemailDeliveryBackend
{
    private const int OpusResamplerQuality = 5;

    public async Task<(string Content, List<Tag> Tags, string Description)> BuildContentAsync(VoicemailAudioJob job, CancellationToken ct)
    {
        var (audioBytes, mimeType) = await LoadAudioAsync(job, ct);
        var dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(audioBytes)}";
        var content =
            $"🎤 Voicemail from {job.CallerNumber} ({job.DurationSeconds}s) - the call wasn't answered.\n\n{dataUri}";
        var tags = new List<Tag>
        {
            Tag.Parse(["alt", "sip2nostr voicemail"]),
            Tag.Parse(["duration", job.DurationSeconds.ToString()]),
        };
        return (content, tags, $"voicemail ({job.DurationSeconds}s, {audioBytes.Length} bytes, {mimeType})");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // No WAV fallback if Opus encoding fails - see docs/voicemail.md.
    private async Task<(byte[] AudioBytes, string MimeType)> LoadAudioAsync(VoicemailAudioJob job, CancellationToken ct)
    {
        var wavBytes = await File.ReadAllBytesAsync(job.WavPath, ct);
        var audioBytes = EncodeOpusOgg(wavBytes, job.SampleRate);

        // The real enforcement against encoded size - MaxRecordingSeconds
        // is only a heuristic ceiling on the configured value.
        if (audioBytes.Length > VoicemailBudget.MaxAudioBytes)
        {
            throw new InvalidOperationException(
                $"Encoded voicemail is {audioBytes.Length} bytes, over the {VoicemailBudget.MaxAudioBytes}-byte NIP-17 budget; sending it would fail.");
        }

        return (audioBytes, "audio/ogg");
    }

    private byte[] EncodeOpusOgg(byte[] wavBytes, int sampleRate)
    {
        var samples = WavEncoder.Decode(wavBytes);

        using var encoder = OpusCodecFactory.CreateEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = VoicemailBudget.OpusBitrateBps;

        // CBR, not VBR (the Concentus/libopus default) - see docs/voicemail.md.
        encoder.UseVBR = false;

        // DTX deliberately not enabled - see docs/voicemail.md.

        using var outputStream = new MemoryStream();

        // No using: OpusOggWriteStream isn't IDisposable - see docs/voicemail.md.
        var oggWriter = new OpusOggWriteStream(encoder, outputStream, new OpusTags(), sampleRate, OpusResamplerQuality, leaveOpen: true);
        oggWriter.WriteSamples(samples, 0, samples.Length);
        oggWriter.Finish();

        var oggBytes = outputStream.ToArray();
        logger.Information("Encoded voicemail as Opus/OGG ({AudioBytes} bytes).", oggBytes.Length);
        return oggBytes;
    }
}
