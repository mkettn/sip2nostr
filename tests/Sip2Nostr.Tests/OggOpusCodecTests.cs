using Sip2Nostr.Sip;
using Xunit;

namespace Sip2Nostr.Tests;

public class OggOpusCodecTests
{
    private const int SampleRate = 8000;
    private const int BitrateBps = 8000;
    private const int ResamplerQuality = 5;

    [Fact]
    public void Encode_ProducesAnOggContainer()
    {
        var samples = MakeToneSamples(SampleRate);

        var ogg = OggOpusCodec.Encode(samples, SampleRate, BitrateBps, ResamplerQuality);

        Assert.True(ogg.Length > 4);
        Assert.Equal("OggS", System.Text.Encoding.ASCII.GetString(ogg, 0, 4));
    }

    [Fact]
    public void Encode_EmptySamples_StillProducesAValidContainer()
    {
        var ogg = OggOpusCodec.Encode([], SampleRate, BitrateBps, ResamplerQuality);

        Assert.Equal("OggS", System.Text.Encoding.ASCII.GetString(ogg, 0, 4));

        // Opus decodes a few priming samples even for an empty input, so
        // this just checks decoding doesn't throw and stays tiny.
        Assert.InRange(OggOpusCodec.Decode(ogg, SampleRate).Length, 0, 400);
    }

    [Fact]
    public void Decode_RoundTripsEncodeAtTheSameSampleRate()
    {
        var samples = MakeToneSamples(SampleRate);

        var decoded = OggOpusCodec.Decode(OggOpusCodec.Encode(samples, SampleRate, BitrateBps, ResamplerQuality), SampleRate);

        // Lossy codec, and Opus decodes a few extra priming samples at the
        // start of the stream - so this checks duration is preserved
        // within a small tolerance rather than an exact sample count.
        Assert.InRange(decoded.Length, samples.Length, samples.Length + 400);
    }

    [Fact]
    public void Decode_AtADifferentSampleRateThanEncoded_ScalesSampleCountAccordingly()
    {
        var samples = MakeToneSamples(SampleRate);

        var decoded = OggOpusCodec.Decode(OggOpusCodec.Encode(samples, SampleRate, BitrateBps, ResamplerQuality), 16000);

        Assert.InRange(decoded.Length, samples.Length * 2, samples.Length * 2 + 800);
    }

    // Regression test for a real bug: a byte string that isn't a
    // decodable Opus stream at all (most commonly in practice: a ".ogg"
    // file that's actually Ogg Vorbis - the traditional meaning of that
    // extension, and a different codec Concentus doesn't handle) must
    // fail loudly, not silently decode to zero samples.
    // OpusOggReadStream.DecodeNextPacket() returns null rather than
    // throwing when a packet doesn't parse as Opus, so this has to be
    // checked explicitly - see Sip/OggOpusCodec.cs.
    [Fact]
    public void Decode_NotAnOpusStream_Throws()
    {
        var notOpus = System.Text.Encoding.ASCII.GetBytes("this is not an ogg/opus file at all, just plain text");

        Assert.Throws<InvalidDataException>(() => OggOpusCodec.Decode(notOpus, SampleRate));
    }

    [Fact]
    public void Decode_EmptyBytes_Throws()
    {
        Assert.Throws<InvalidDataException>(() => OggOpusCodec.Decode([], SampleRate));
    }

    private static short[] MakeToneSamples(int sampleRate)
    {
        var samples = new short[sampleRate]; // 1 second
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(Math.Sin(i * 0.05) * 10000);
        }

        return samples;
    }
}
