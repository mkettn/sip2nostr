using Sip2Nostr.Sip;
using Xunit;

namespace Sip2Nostr.Tests;

public class WavEncoderTests
{
    [Fact]
    public void Encode_ProducesValidHeaderAndSampleData()
    {
        short[] samples = [1, -1, short.MaxValue, short.MinValue, 0];

        var wav = WavEncoder.Encode(samples, 8000);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));

        var channels = BitConverter.ToInt16(wav, 22);
        var sampleRate = BitConverter.ToInt32(wav, 24);
        var bitsPerSample = BitConverter.ToInt16(wav, 34);
        var dataLength = BitConverter.ToInt32(wav, 40);

        Assert.Equal(1, channels);
        Assert.Equal(8000, sampleRate);
        Assert.Equal(16, bitsPerSample);
        Assert.Equal(samples.Length * sizeof(short), dataLength);
        Assert.Equal(44 + dataLength, wav.Length);

        for (var i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i], BitConverter.ToInt16(wav, 44 + i * sizeof(short)));
        }
    }

    [Fact]
    public void Encode_EmptySamples_ProducesHeaderOnly()
    {
        var wav = WavEncoder.Encode([], 8000);

        Assert.Equal(44, wav.Length);
        Assert.Equal(0, BitConverter.ToInt32(wav, 40));
    }

    [Fact]
    public void Decode_RoundTripsEncode()
    {
        short[] samples = [1, -1, short.MaxValue, short.MinValue, 0, 12345, -12345];

        var decoded = WavEncoder.Decode(WavEncoder.Encode(samples, 8000));

        Assert.Equal(samples, decoded);
    }

    [Fact]
    public void Decode_EmptySamples_RoundTrips()
    {
        var decoded = WavEncoder.Decode(WavEncoder.Encode([], 8000));

        Assert.Empty(decoded);
    }

    [Fact]
    public void Decode_TooShortForHeader_Throws()
    {
        Assert.Throws<ArgumentException>(() => WavEncoder.Decode(new byte[WavEncoder.HeaderLength - 1]));
    }
}
