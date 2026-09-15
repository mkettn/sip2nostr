using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using Sip2Nostr.Shared;
using Xunit;

namespace Sip2Nostr.Tests;

public class SoundFileResolverTests
{
    private const int SampleRate = 8000;

    [Fact]
    public void TryResolve_MissingFile_ReturnsFailureReason()
    {
        var (resolvedPath, failureReason) = SoundFileResolver.TryResolve("does-not-exist.opus", Path.GetTempPath());

        Assert.Null(resolvedPath);
        Assert.Contains("does not exist", failureReason);
    }

    [Fact]
    public void TryResolve_UnsupportedExtension_ReturnsFailureReason()
    {
        var path = WriteTempFile("not a sound file", ".mp3");
        try
        {
            var (resolvedPath, failureReason) = SoundFileResolver.TryResolve(path, Path.GetTempPath());

            Assert.Null(resolvedPath);
            Assert.Contains("not a supported format", failureReason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryResolve_RawPcmFile_ResolvesDirectlyWithNoDecoding()
    {
        var path = WriteTempFile("raw-pcm-bytes", ".pcm");
        try
        {
            var (resolvedPath, failureReason) = SoundFileResolver.TryResolve(path, Path.GetTempPath());

            Assert.Null(failureReason);
            Assert.Equal(path, resolvedPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryResolve_MonoOpusFile_Decodes()
    {
        var path = WriteOpusFile(EncodeOpus(MakeToneSamples(), channels: 1));
        try
        {
            var (resolvedPath, failureReason) = SoundFileResolver.TryResolve(path, Path.GetTempPath());

            Assert.Null(failureReason);
            Assert.NotNull(resolvedPath);
            Assert.True(File.Exists(resolvedPath));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Regression test: nothing in SoundFileResolver/OpusCodec inspects an
    // .opus file's channel count - only the extension is checked, and
    // OpusCodec.Decode always decodes via a mono decoder regardless of how
    // the stream was actually encoded. Concentus/libopus downmixes a
    // stereo stream through that mono decoder rather than rejecting or
    // corrupting it (confirmed here, not just assumed) - so a stereo
    // .opus file is accepted too, not just the mono files the error
    // messages used to claim were the only ones "supported".
    [Fact]
    public void TryResolve_StereoOpusFile_AlsoDecodes()
    {
        var stereoSamples = MakeInterleavedStereoSamples();
        var path = WriteOpusFile(EncodeOpus(stereoSamples, channels: 2));
        try
        {
            var (resolvedPath, failureReason) = SoundFileResolver.TryResolve(path, Path.GetTempPath());

            Assert.Null(failureReason);
            Assert.NotNull(resolvedPath);
            Assert.True(File.Exists(resolvedPath));

            // Downmixed, not silently discarded: the decoded mono PCM
            // actually carries the left channel's tone.
            var decodedBytes = File.ReadAllBytes(resolvedPath);
            Assert.True(decodedBytes.Length > 0);
            var hasNonZeroSample = false;
            for (var i = 0; i + 1 < decodedBytes.Length; i += 2)
            {
                if (BitConverter.ToInt16(decodedBytes, i) != 0)
                {
                    hasNonZeroSample = true;
                    break;
                }
            }

            Assert.True(hasNonZeroSample);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryResolve_CorruptOpusFile_ReturnsFailureReason()
    {
        var path = WriteTempFile("this is not actually opus data", ".opus");
        try
        {
            var (resolvedPath, failureReason) = SoundFileResolver.TryResolve(path, Path.GetTempPath());

            Assert.Null(resolvedPath);
            Assert.Contains("Could not decode", failureReason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static short[] MakeToneSamples()
    {
        var samples = new short[SampleRate];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(Math.Sin(i * 0.05) * 10000);
        }

        return samples;
    }

    // Interleaved L,R,L,R,... - left channel carries a tone, right is
    // silent, so a successful downmix must show up as nonzero output.
    private static short[] MakeInterleavedStereoSamples()
    {
        var mono = MakeToneSamples();
        var stereo = new short[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            stereo[i * 2] = mono[i];
            stereo[i * 2 + 1] = 0;
        }

        return stereo;
    }

    // OpusCodec.Encode (Sip/OpusCodec.cs) only ever encodes mono - this
    // bypasses it to produce a real multi-channel Opus stream, the same
    // way an operator's own stereo-encoded file would look.
    private static byte[] EncodeOpus(short[] samples, int channels)
    {
        using var encoder = OpusCodecFactory.CreateEncoder(SampleRate, channels, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 32000;
        encoder.UseVBR = false;

        using var outputStream = new MemoryStream();
        var writer = new OpusOggWriteStream(encoder, outputStream, new OpusTags(), SampleRate, resamplerQuality: 5, leaveOpen: true);
        writer.WriteSamples(samples, 0, samples.Length);
        writer.Finish();

        return outputStream.ToArray();
    }

    private static string WriteOpusFile(byte[] opusBytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sip2nostr-test-{Guid.NewGuid():N}.opus");
        File.WriteAllBytes(path, opusBytes);
        return path;
    }

    private static string WriteTempFile(string content, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sip2nostr-test-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content);
        return path;
    }
}
