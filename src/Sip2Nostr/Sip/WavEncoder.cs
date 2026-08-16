namespace Sip2Nostr.Sip;

// Minimal canonical 16-bit PCM WAV header - just enough for ffmpeg or any
// player to read a mono voicemail recording. No extension chunks.
public static class WavEncoder
{
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    public static byte[] Encode(short[] samples, int sampleRate)
    {
        var dataLength = samples.Length * sizeof(short);
        var byteRate = sampleRate * Channels * BitsPerSample / 8;
        var blockAlign = Channels * BitsPerSample / 8;

        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataLength);
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
}
