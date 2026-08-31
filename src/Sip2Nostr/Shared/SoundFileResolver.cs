using System.Security.Cryptography;
using System.Text;
using Serilog;
using Sip2Nostr.Sip;

namespace Sip2Nostr.Shared;

// Resolves a configured sound path (relative to the config file's
// directory unless rooted) to a playable raw 8 kHz mono 16-bit PCM file,
// decoding mono Opus in-process via OpusCodec and caching the result if
// needed. Shared by [[lines]].sound (LocalTestAudioSink) and
// [voicemail].greeting_sound (VoicemailSink) - see docs/voicemail.md.
public static class SoundFileResolver
{
    private const int PlaybackSampleRate = 8000;

    public static string? Resolve(string soundPath, string configDirectory, ILogger logger)
    {
        var resolvedSoundPath = Path.IsPathRooted(soundPath)
            ? soundPath
            : Path.GetFullPath(Path.Combine(configDirectory, soundPath));

        if (!File.Exists(resolvedSoundPath))
        {
            logger.Warning(
                "Configured sound file {SoundPath} resolved to {ResolvedSoundPath}, but it does not exist.",
                soundPath,
                resolvedSoundPath);
            return null;
        }

        if (IsRawPcmPath(resolvedSoundPath))
        {
            return resolvedSoundPath;
        }

        if (!IsOpusPath(resolvedSoundPath))
        {
            logger.Warning(
                "Configured sound file {SoundPath} is not a supported format; only raw 8 kHz 16-bit PCM " +
                "(.pcm/.raw/.s16le) and mono Opus (.opus) files are supported.",
                soundPath);
            return null;
        }

        return DecodeOpusToRawPcm(resolvedSoundPath, logger);
    }

    private static string? DecodeOpusToRawPcm(string soundPath, ILogger logger)
    {
        var cachePath = GetConvertedSoundPath(soundPath);
        if (File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(soundPath))
        {
            return cachePath;
        }

        try
        {
            var opusBytes = File.ReadAllBytes(soundPath);
            var samples = OpusCodec.Decode(opusBytes, PlaybackSampleRate);

            var pcmBytes = new byte[samples.Length * sizeof(short)];
            Buffer.BlockCopy(samples, 0, pcmBytes, 0, pcmBytes.Length);

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllBytes(cachePath, pcmBytes);

            logger.Information("Decoded {SoundPath} to raw 8 kHz PCM at {ConvertedSoundPath}.", soundPath, cachePath);
            return cachePath;
        }
        catch (Exception exception)
        {
            logger.Warning(exception, "Could not decode {SoundPath}; provide a mono Opus file or raw 8 kHz 16-bit PCM.", soundPath);
            return null;
        }
    }

    private static bool IsRawPcmPath(string soundPath)
    {
        var extension = Path.GetExtension(soundPath);
        return extension.Equals(".pcm", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".raw", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".s16le", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOpusPath(string soundPath) =>
        Path.GetExtension(soundPath).Equals(".opus", StringComparison.OrdinalIgnoreCase);

    private static string GetConvertedSoundPath(string soundPath)
    {
        var fullPath = Path.GetFullPath(soundPath);
        var lastWriteTime = File.GetLastWriteTimeUtc(fullPath).Ticks;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{fullPath}:{lastWriteTime}")))[..16];
        return Path.Combine(Path.GetTempPath(), "sip2nostr", "sounds", $"{hash}.s16le");
    }
}
