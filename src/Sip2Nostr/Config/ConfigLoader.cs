using Tomlyn;

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

    // Fails fast at startup instead of surfacing as a confusing runtime
    // symptom mid-call - an unchecked non-positive value here turns into
    // an ArgumentOutOfRangeException from Task.Delay deep inside
    // CallBridge, which the broad catch around Nostr signaling reports as
    // "signaling failed", silently sending every call straight to
    // voicemail with the wrong diagnosis.
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
    }
}
