using Tomlyn;
using Sip2Nostr.Shared;

namespace Sip2Nostr.Config;

public static class ConfigLoader
{
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Config file not found: {path}", path);
        }

        var toml = File.ReadAllText(path);
        var config = TomlSerializer.Deserialize<AppConfig>(toml, TomlSerializerOptions.Default)
            ?? throw new InvalidDataException($"Config file '{path}' deserialized to an empty document.");
        config.ConfigDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
        Validate(config);
        return config;
    }

    // See docs/voicemail.md for why these fail fast here.
    private static void Validate(AppConfig config)
    {
        if (config.Voicemail.RingTimeoutSeconds <= 0)
        {
            throw new InvalidDataException(
                $"[voicemail].ring_timeout_seconds must be greater than 0, got {config.Voicemail.RingTimeoutSeconds}.");
        }

        if (config.Voicemail.MaxRecordingSeconds <= 0)
        {
            throw new InvalidDataException(
                $"[voicemail].max_recording_seconds must be greater than 0, got {config.Voicemail.MaxRecordingSeconds}.");
        }

        // Only a constraint on the "audio" backend - a transcript is tiny
        // regardless of how long the recording was, so "text" delivery
        // isn't bound by the Opus/NIP-17 size budget this checks against.
        if (config.Voicemail.Delivery == "audio" &&
            config.Voicemail.MaxRecordingSeconds > VoicemailBudget.MaxRecordingSeconds)
        {
            throw new InvalidDataException(
                $"[voicemail].max_recording_seconds is {config.Voicemail.MaxRecordingSeconds}, but a recording that " +
                $"long can never fit in a single NIP-17 DM at the current Opus encoding - the maximum that reliably " +
                $"fits is {VoicemailBudget.MaxRecordingSeconds}s. See docs/voicemail.md.");
        }

        if (config.Voicemail.Delivery is not ("audio" or "text"))
        {
            throw new InvalidDataException(
                $"[voicemail].delivery must be \"audio\" or \"text\", got \"{config.Voicemail.Delivery}\".");
        }

        if (config.Voicemail.Delivery == "text")
        {
            if (config.Voicemail.Transcription.Engine != "whisper")
            {
                throw new InvalidDataException(
                    $"[voicemail.transcription].engine \"{config.Voicemail.Transcription.Engine}\" is not supported - only \"whisper\" is available today.");
            }

            if (string.IsNullOrWhiteSpace(config.Voicemail.Transcription.ModelPath))
            {
                throw new InvalidDataException(
                    "[voicemail.transcription].model_path is required when [voicemail].delivery = \"text\".");
            }

            var resolvedModelPath = Path.IsPathRooted(config.Voicemail.Transcription.ModelPath)
                ? config.Voicemail.Transcription.ModelPath
                : Path.GetFullPath(Path.Combine(config.ConfigDirectory, config.Voicemail.Transcription.ModelPath));
            if (!File.Exists(resolvedModelPath))
            {
                throw new InvalidDataException(
                    $"[voicemail.transcription].model_path \"{config.Voicemail.Transcription.ModelPath}\" resolved to " +
                    $"\"{resolvedModelPath}\", but no file exists there.");
            }
        }
    }
}
