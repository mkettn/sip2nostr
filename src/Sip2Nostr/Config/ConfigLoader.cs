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

        if (config.Voicemail.Delivery is not ("audio" or "text"))
        {
            throw new InvalidDataException(
                $"[voicemail].delivery must be \"audio\" or \"text\", got \"{config.Voicemail.Delivery}\".");
        }

        if (string.IsNullOrWhiteSpace(config.Voicemail.RecordingFilename))
        {
            throw new InvalidDataException("[voicemail].recording_filename must not be empty.");
        }

        // Without {timestamp} or {call_id}, every recording would resolve
        // to the same filename and silently overwrite the last one.
        if (!config.Voicemail.RecordingFilename.Contains("{timestamp}", StringComparison.OrdinalIgnoreCase) &&
            !config.Voicemail.RecordingFilename.Contains("{call_id}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "[voicemail].recording_filename must include {timestamp} or {call_id}, so recordings from " +
                $"different calls can't overwrite each other. Got \"{config.Voicemail.RecordingFilename}\".");
        }

        // The ceiling depends on which backend is actually recording-length
        // sensitive: "audio" is bound by the Opus/NIP-17 size budget below;
        // "text" isn't (a transcript stays small regardless - the real
        // enforcement there is MaxTranscriptBytes, checked against the
        // actual output at send time), but recording length still needs
        // *some* sanity ceiling so VoicemailSink's in-memory PCM buffer
        // can't grow unbounded. See docs/voicemail.md.
        var maxRecordingSecondsCeiling = config.Voicemail.Delivery == "text"
            ? VoicemailBudget.MaxTextRecordingSeconds
            : VoicemailBudget.MaxRecordingSeconds;
        if (config.Voicemail.MaxRecordingSeconds > maxRecordingSecondsCeiling)
        {
            throw new InvalidDataException(
                $"[voicemail].max_recording_seconds is {config.Voicemail.MaxRecordingSeconds}, but the maximum for " +
                $"[voicemail].delivery = \"{config.Voicemail.Delivery}\" is {maxRecordingSecondsCeiling}s. See docs/voicemail.md.");
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
