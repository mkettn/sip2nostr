using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Xunit;

namespace Sip2Nostr.Tests;

// Modem/ModemCallAudio.cs round-trips raw ALSA PCM through
// SIPSorcery.Media.AudioEncoder to G.711 and back, since (unlike the SIP
// leg) there is no RTPSession to relay raw payloads from - see
// docs/hub-architecture.md's "Why RTP, not PCM" section. This locks in that
// the encode/decode pair actually round-trips for the format/rate the
// modem source uses.
public class ModemAudioCodecRoundTripTests
{
    private static readonly AudioFormat Pcma = new(SDPWellKnownMediaFormatsEnum.PCMA);

    [Fact]
    public void EncodeThenDecode_PreservesSampleCount()
    {
        var encoder = new AudioEncoder();
        var pcm = new short[160]; // 20ms @ 8kHz
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)(Math.Sin(i * 0.2) * 10000);
        }

        var encoded = encoder.EncodeAudio(pcm, Pcma);
        Assert.Equal(pcm.Length, encoded.Length); // G.711 is 1 byte/sample

        var decoded = encoder.DecodeAudio(encoded, Pcma);
        Assert.Equal(pcm.Length, decoded.Length);
    }

    [Fact]
    public void EncodeThenDecode_RoundTripsWithinG711QuantizationError()
    {
        var encoder = new AudioEncoder();
        short[] pcm = [0, 1000, -1000, 32000, -32000, 100, -100];

        var decoded = encoder.DecodeAudio(encoder.EncodeAudio(pcm, Pcma), Pcma);

        for (var i = 0; i < pcm.Length; i++)
        {
            // A-law is a lossy, companded codec; some quantization error is
            // expected and not a bug.
            Assert.True(Math.Abs(pcm[i] - decoded[i]) < 1500, $"sample {i}: {pcm[i]} vs {decoded[i]}");
        }
    }
}
