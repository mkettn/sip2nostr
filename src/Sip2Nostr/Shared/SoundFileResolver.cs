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

    public static string? Resolve(string soundPath, string configDirectory, ILogger logger) =>
        TryResolve(soundPath, configDirectory, logger).ResolvedPath;

    // Same resolution as Resolve, but always returns the failure reason
    // alongside the null result, rather than only ever logging it -
    // ConfigLoader validates sound files before the "real" (run-file)
    // logger exists (see Program.cs), so a Warning logged there would only
    // ever reach the console, never wherever an operator actually looks
    // for it after a crashed startup. logger is optional: when given,
    // Resolve's own behavior (Warning on failure, Information on a fresh
    // Opus decode) is unchanged; ConfigLoader omits it and embeds
    // FailureReason directly in its own exception message instead.
    public static (string? ResolvedPath, string? FailureReason) TryResolve(string soundPath, string configDirectory, ILogger? logger = null)
    {
        var resolvedSoundPath = Path.IsPathRooted(soundPath)
            ? soundPath
            : Path.GetFullPath(Path.Combine(configDirectory, soundPath));

        if (!File.Exists(resolvedSoundPath))
        {
            return Fail(logger, $"Configured sound file {soundPath} resolved to {resolvedSoundPath}, but it does not exist.");
        }

        if (IsRawPcmPath(resolvedSoundPath))
        {
            return (resolvedSoundPath, null);
        }

        if (!IsOpusPath(resolvedSoundPath))
        {
            return Fail(
                logger,
                $"Configured sound file {soundPath} is not a supported format; only raw 8 kHz 16-bit PCM " +
                "(.pcm/.raw/.s16le) and mono Opus (.opus) files are supported.");
        }

        return DecodeOpusToRawPcm(resolvedSoundPath, logger);
    }

    private static (string? ResolvedPath, string? FailureReason) DecodeOpusToRawPcm(string soundPath, ILogger? logger)
    {
        var cachePath = GetConvertedSoundPath(soundPath);
        if (File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(soundPath))
        {
            return (cachePath, null);
        }

        try
        {
            var opusBytes = File.ReadAllBytes(soundPath);
            var samples = OpusCodec.Decode(opusBytes, PlaybackSampleRate);

            var pcmBytes = new byte[samples.Length * sizeof(short)];
            Buffer.BlockCopy(samples, 0, pcmBytes, 0, pcmBytes.Length);

            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllBytes(cachePath, pcmBytes);

            logger?.Information("Decoded {SoundPath} to raw 8 kHz PCM at {ConvertedSoundPath}.", soundPath, cachePath);
            return (cachePath, null);
        }
        catch (Exception exception)
        {
            var reason = $"Could not decode {soundPath}; provide a mono Opus file or raw 8 kHz 16-bit PCM.";
            logger?.Warning(exception, "{FailureReason}", reason);
            return (null, $"{reason} ({exception.Message})");
        }
    }

    private static (string? ResolvedPath, string? FailureReason) Fail(ILogger? logger, string reason)
    {
        logger?.Warning("{FailureReason}", reason);
        return (null, reason);
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
