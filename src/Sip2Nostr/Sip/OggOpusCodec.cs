using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;

namespace Sip2Nostr.Sip;

// Encodes/decodes 16-bit mono PCM as Ogg-Opus entirely in-process via
// Concentus (a pure C# port of libopus) and Concentus.Oggfile - no
// external process dependency. Used both for voicemail recordings
// (Sinks/VoicemailSink.cs) and for decoding configured sound files
// (Shared/SoundFileResolver.cs).
public static class OggOpusCodec
{
    public static byte[] Encode(short[] samples, int sampleRate, int bitrateBps, int resamplerQuality)
    {
        using var encoder = OpusCodecFactory.CreateEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = bitrateBps;

        // CBR, not VBR (the Concentus/libopus default) - Bitrate would
        // otherwise only be a target the encoder can exceed on complex
        // input, undermining a size budget checked against it.
        encoder.UseVBR = false;

        using var outputStream = new MemoryStream();

        // No using: OpusOggWriteStream isn't IDisposable - Finish() is
        // what pads the trailing frame, writes the end-of-stream page,
        // and flushes; leaveOpen keeps outputStream readable afterwards.
        var oggWriter = new OpusOggWriteStream(encoder, outputStream, new OpusTags(), sampleRate, resamplerQuality, leaveOpen: true);
        oggWriter.WriteSamples(samples, 0, samples.Length);
        oggWriter.Finish();

        return outputStream.ToArray();
    }

    // sampleRate selects the decoder's own output rate, independent of
    // what the file was encoded at - Opus decodes natively at any of
    // 8/12/16/24/48 kHz, so a caller needing a specific rate (playback
    // at 8 kHz, Whisper at 16 kHz) gets it directly with no separate
    // resampling step.
    public static short[] Decode(byte[] oggBytes, int sampleRate)
    {
        using var decoder = OpusCodecFactory.CreateDecoder(sampleRate, 1);
        using var inputStream = new MemoryStream(oggBytes);
        var oggReader = new OpusOggReadStream(decoder, inputStream);

        var samples = new List<short>();
        while (oggReader.HasNextPacket)
        {
            var packet = oggReader.DecodeNextPacket();
            if (packet is not null)
            {
                samples.AddRange(packet);
            }
        }

        return samples.ToArray();
    }
}
