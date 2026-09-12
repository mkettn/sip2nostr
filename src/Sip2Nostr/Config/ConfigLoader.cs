using Nostr.Sdk;
using Serilog;
using Tomlyn;
using Sip2Nostr.Shared;

namespace Sip2Nostr.Config;

public static class ConfigLoader
{
    // logger is a bootstrap, console-only Serilog logger built before this
    // config is loaded (see Program.cs) - the real one, with the run-file
    // sink this config itself configures, doesn't exist yet. Needed here
    // because eagerly resolving [[lines]].sound / [voicemail].greeting_sound
    // (SoundFileResolver.Resolve) logs its own diagnostics.
    public static AppConfig Load(string path, ILogger logger)
    {
        if (!File.Exists(path))
        {
            throw new ConfigurationException($"Config file not found: {path}");
        }

        AppConfig? config;
        try
        {
            var toml = File.ReadAllText(path);
            config = TomlSerializer.Deserialize<AppConfig>(toml, TomlSerializerOptions.Default);
        }
        catch (TomlException exception)
        {
            throw new ConfigurationException($"Config file '{path}' could not be parsed: {exception.Message}", exception);
        }

        if (config is null)
        {
            throw new ConfigurationException($"Config file '{path}' deserialized to an empty document.");
        }

        config.ConfigDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
        Validate(config, logger);
        return config;
    }

    // See docs/voicemail.md for why these fail fast here.
    private static void Validate(AppConfig config, ILogger logger)
    {
        if (config.Voicemail.RingTimeoutSeconds <= 0)
        {
            throw new ConfigurationException(
                $"[voicemail].ring_timeout_seconds must be greater than 0, got {config.Voicemail.RingTimeoutSeconds}.");
        }

        if (config.WebRtc.ConnectionLossGraceSeconds <= 0)
        {
            throw new ConfigurationException(
                "[webrtc].connection_loss_grace_seconds must be greater than 0, got " +
                $"{config.WebRtc.ConnectionLossGraceSeconds}.");
        }

        if (config.Voicemail.MaxRecordingSeconds <= 0)
        {
            throw new ConfigurationException(
                $"[voicemail].max_recording_seconds must be greater than 0, got {config.Voicemail.MaxRecordingSeconds}.");
        }

        if (config.Voicemail.MaxTextRecordingSeconds is <= 0 or > VoicemailBudget.MaxTextRecordingSecondsCeiling)
        {
            throw new ConfigurationException(
                "[voicemail].max_text_recording_seconds must be greater than 0 and at most " +
                $"{VoicemailBudget.MaxTextRecordingSecondsCeiling}, got {config.Voicemail.MaxTextRecordingSeconds}.");
        }

        if (config.Voicemail.OpusResamplerQuality is < 0 or > 10)
        {
            throw new ConfigurationException(
                $"[voicemail].opus_resampler_quality must be between 0 and 10, got {config.Voicemail.OpusResamplerQuality}.");
        }

        if (config.Voicemail.Delivery is not ("audio" or "text"))
        {
            throw new ConfigurationException(
                $"[voicemail].delivery must be \"audio\" or \"text\", got \"{config.Voicemail.Delivery}\".");
        }

        if (string.IsNullOrWhiteSpace(config.Voicemail.RecordingFilename))
        {
            throw new ConfigurationException("[voicemail].recording_filename must not be empty.");
        }

        // Without {timestamp} or {call_id}, every recording would resolve
        // to the same filename and silently overwrite the last one.
        if (!config.Voicemail.RecordingFilename.Contains("{timestamp}", StringComparison.OrdinalIgnoreCase) &&
            !config.Voicemail.RecordingFilename.Contains("{call_id}", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigurationException(
                "[voicemail].recording_filename must include {timestamp} or {call_id}, so recordings from " +
                $"different calls can't overwrite each other. Got \"{config.Voicemail.RecordingFilename}\".");
        }

        // VoicemailSink.SaveRecordingAsync joins this straight onto
        // recordings_dir - a rooted template would silently discard
        // recordings_dir entirely (Path.Combine's documented behavior),
        // and a ".." segment could escape it, so both are rejected here
        // rather than only ever naming something under recordings_dir.
        if (Path.IsPathRooted(config.Voicemail.RecordingFilename))
        {
            throw new ConfigurationException(
                "[voicemail].recording_filename must be a relative path, resolved under recordings_dir - " +
                $"got \"{config.Voicemail.RecordingFilename}\".");
        }

        if (config.Voicemail.RecordingFilename.Split('/', '\\').Any(segment => segment == ".."))
        {
            throw new ConfigurationException(
                "[voicemail].recording_filename must not contain \"..\" path segments - " +
                $"got \"{config.Voicemail.RecordingFilename}\".");
        }

        // The ceiling depends on which backend is actually recording-length
        // sensitive: "audio" is bound by the Opus/NIP-17 size budget below
        // (VoicemailBudget.MaxRecordingSeconds - not configurable, derived
        // from that budget); "text" isn't (a transcript stays small
        // regardless - the real enforcement there is MaxTranscriptBytes,
        // checked against the actual output at send time), so its ceiling
        // is just the configurable max_text_recording_seconds sanity limit
        // on VoicemailSink's in-memory PCM buffer. See docs/voicemail.md.
        var maxRecordingSecondsCeiling = config.Voicemail.Delivery == "text"
            ? config.Voicemail.MaxTextRecordingSeconds
            : VoicemailBudget.MaxRecordingSeconds;
        if (config.Voicemail.MaxRecordingSeconds > maxRecordingSecondsCeiling)
        {
            throw new ConfigurationException(
                $"[voicemail].max_recording_seconds is {config.Voicemail.MaxRecordingSeconds}, but the maximum for " +
                $"[voicemail].delivery = \"{config.Voicemail.Delivery}\" is {maxRecordingSecondsCeiling}s. See docs/voicemail.md.");
        }

        if (config.Voicemail.Delivery == "text")
        {
            if (config.Voicemail.Transcription.Engine != "whisper")
            {
                throw new ConfigurationException(
                    $"[voicemail.transcription].engine \"{config.Voicemail.Transcription.Engine}\" is not supported - only \"whisper\" is available today.");
            }

            if (string.IsNullOrWhiteSpace(config.Voicemail.Transcription.ModelPath))
            {
                throw new ConfigurationException(
                    "[voicemail.transcription].model_path is required when [voicemail].delivery = \"text\".");
            }

            var resolvedModelPath = Path.IsPathRooted(config.Voicemail.Transcription.ModelPath)
                ? config.Voicemail.Transcription.ModelPath
                : Path.GetFullPath(Path.Combine(config.ConfigDirectory, config.Voicemail.Transcription.ModelPath));
            if (!File.Exists(resolvedModelPath))
            {
                throw new ConfigurationException(
                    $"[voicemail.transcription].model_path \"{config.Voicemail.Transcription.ModelPath}\" resolved to " +
                    $"\"{resolvedModelPath}\", but no file exists there.");
            }
        }

        ValidateNostrIdentity(config);
        ValidateSoundFiles(config, logger);
    }

    // [nostr].bridge_nsec/target_npub/relays and [voicemail].dm_relays are
    // otherwise only ever parsed deep inside NostrSignalingClient/
    // VoicemailSender, the first time a real call or voicemail actually
    // needs them - so a malformed value (there's no way to fix a bad key
    // by waiting; unlike a relay being briefly unreachable, this never
    // becomes valid) previously went unnoticed until then. See issue #26.
    // .required on these AppConfig properties is a compile-time-only
    // signal - Tomlyn leaves a missing key as null rather than failing
    // deserialization, so a null/empty check is needed before handing
    // these to Nostr.Sdk, which throws ArgumentNullException (not the
    // NostrSdkException a genuinely malformed value throws) for null.
    private static void ValidateNostrIdentity(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Nostr.BridgeNsec))
        {
            throw new ConfigurationException("[nostr].bridge_nsec must not be empty.");
        }

        try
        {
            Keys.Parse(config.Nostr.BridgeNsec);
        }
        catch (NostrSdkException exception)
        {
            throw new ConfigurationException(
                $"[nostr].bridge_nsec is not a valid Nostr private key (nsec or hex): {exception.Message}", exception);
        }

        if (string.IsNullOrWhiteSpace(config.Nostr.TargetNpub))
        {
            throw new ConfigurationException("[nostr].target_npub must not be empty.");
        }

        try
        {
            PublicKey.Parse(config.Nostr.TargetNpub);
        }
        catch (NostrSdkException exception)
        {
            throw new ConfigurationException(
                $"[nostr].target_npub is not a valid Nostr public key (npub or hex): {exception.Message}", exception);
        }

        if (config.Nostr.Relays is null or { Count: 0 })
        {
            throw new ConfigurationException("[nostr].relays must contain at least one relay URL.");
        }

        ValidateRelayUrls(config.Nostr.Relays, "[nostr].relays");
        ValidateRelayUrls(config.Voicemail.DmRelays, "[voicemail].dm_relays");
    }

    private static void ValidateRelayUrls(List<string> relays, string keyName)
    {
        foreach (var relay in relays)
        {
            try
            {
                RelayUrl.Parse(relay);
            }
            catch (NostrSdkException exception)
            {
                throw new ConfigurationException($"{keyName} entry \"{relay}\" is not a valid relay URL: {exception.Message}", exception);
            }
        }
    }

    // [[lines]].sound and [voicemail].greeting_sound used to be resolved
    // lazily, the first time a sink actually needed to play the file - a
    // bad path/format/decode failure just fell back to a sine-wave tone,
    // logged at Warning, discoverable only by placing a call. Resolving
    // eagerly here reuses that same Warning-logging Resolve rather than a
    // separate non-logging check, so the specific reason (missing file,
    // unsupported format, decode failure) is still in the log immediately
    // above the exception that stops startup.
    private static void ValidateSoundFiles(AppConfig config, ILogger logger)
    {
        foreach (var line in config.Lines ?? [])
        {
            if (!string.IsNullOrWhiteSpace(line.Sound) &&
                SoundFileResolver.Resolve(line.Sound, config.ConfigDirectory, logger) is null)
            {
                throw new ConfigurationException(
                    $"[[lines]] \"{line.Label}\" sound \"{line.Sound}\" could not be resolved as a playable sound file - see the warning above for why.");
            }
        }

        if (!string.IsNullOrWhiteSpace(config.Voicemail.GreetingSound) &&
            SoundFileResolver.Resolve(config.Voicemail.GreetingSound, config.ConfigDirectory, logger) is null)
        {
            throw new ConfigurationException(
                $"[voicemail].greeting_sound \"{config.Voicemail.GreetingSound}\" could not be resolved as a playable sound file - see the warning above for why.");
        }
    }
}
