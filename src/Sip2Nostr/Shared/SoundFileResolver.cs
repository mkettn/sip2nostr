using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace Sip2Nostr.Shared;

// Resolves a configured sound path (relative to the config file's
// directory unless rooted) to a playable raw 8 kHz mono 16-bit PCM file,
// converting via ffmpeg and caching the result if needed. Shared by
// [[lines]].sound (LocalTestAudioSink) and [voicemail].greeting_sound
// (VoicemailSink) - see docs/voicemail.md for the ffmpeg fallback story.
public static class SoundFileResolver
{
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

        return ConvertSoundToRawPcm(resolvedSoundPath, logger);
    }

    private static string? ConvertSoundToRawPcm(string soundPath, ILogger logger)
    {
        var cachePath = GetConvertedSoundPath(soundPath);
        if (File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(soundPath))
        {
            return cachePath;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList =
                {
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-y",
                    "-i",
                    soundPath,
                    "-ac",
                    "1",
                    "-ar",
                    "8000",
                    "-f",
                    "s16le",
                    cachePath,
                },
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                logger.Warning("Could not start ffmpeg to convert {SoundPath}.", soundPath);
                return null;
            }

            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                logger.Warning(
                    "ffmpeg failed to convert {SoundPath} to raw PCM: {FfmpegError}",
                    soundPath,
                    error.Trim());
                return null;
            }

            logger.Information("Converted {SoundPath} to raw 8 kHz PCM at {ConvertedSoundPath}.", soundPath, cachePath);
            return cachePath;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.Warning(exception, "Could not convert {SoundPath}; install ffmpeg or provide raw 8 kHz 16-bit PCM.", soundPath);
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

    private static string GetConvertedSoundPath(string soundPath)
    {
        var fullPath = Path.GetFullPath(soundPath);
        var lastWriteTime = File.GetLastWriteTimeUtc(fullPath).Ticks;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{fullPath}:{lastWriteTime}")))[..16];
        return Path.Combine(Path.GetTempPath(), "sip2nostr", "sounds", $"{hash}.s16le");
    }
}
