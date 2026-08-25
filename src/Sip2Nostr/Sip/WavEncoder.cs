namespace Sip2Nostr.Sip;

// Minimal canonical 16-bit PCM WAV header - just enough for ffmpeg or any
// player to read a mono voicemail recording. No extension chunks.
public static class WavEncoder
{
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

    // Not a general WAV parser - only decodes Encode's own output.
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
