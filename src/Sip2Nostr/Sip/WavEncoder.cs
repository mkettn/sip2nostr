namespace Sip2Nostr.Sip;

// Minimal canonical 16-bit PCM WAV header - just enough for ffmpeg or any
// player to read a mono voicemail recording. No extension chunks.
public static class WavEncoder
{
    // Public and used by both Encode and Decode (and by VoicemailSender,
    // which needs to recover the PCM samples this class wrote) so the two
    // never drift apart - this is the whole header with no chunks beyond
    // fmt/data, always exactly this many bytes for anything Encode wrote.
    public const int HeaderLength = 44;

    private const int BitsPerSample = 16;
    private const int Channels = 1;

    public static byte[] Encode(short[] samples, int sampleRate)
    {
        var dataLength = samples.Length * sizeof(short);
        var byteRate = sampleRate * Channels * BitsPerSample / 8;
        var blockAlign = Channels * BitsPerSample / 8;

        using var stream = new MemoryStream(HeaderLength + dataLength);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(HeaderLength - 8 + dataLength);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16); // fmt chunk size
        writer.Write((short)1); // PCM
        writer.Write((short)Channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)BitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLength);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        return stream.ToArray();
    }

    // The counterpart to Encode - recovers the PCM samples from a WAV
    // buffer this class produced. Not a general WAV parser: it trusts the
    // header is exactly HeaderLength bytes of the layout Encode writes,
    // which is true for anything that came from Encode (the only producer
    // in this codebase), and throws rather than silently misreading a
    // shorter/foreign buffer as audio.
    public static short[] Decode(byte[] wav)
    {
        if (wav.Length < HeaderLength)
        {
            throw new ArgumentException(
                $"WAV data is only {wav.Length} byte(s), shorter than the {HeaderLength}-byte header.",
                nameof(wav));
        }

        var sampleCount = (wav.Length - HeaderLength) / sizeof(short);
        var samples = new short[sampleCount];
        Buffer.BlockCopy(wav, HeaderLength, samples, 0, sampleCount * sizeof(short));
        return samples;
    }
}
