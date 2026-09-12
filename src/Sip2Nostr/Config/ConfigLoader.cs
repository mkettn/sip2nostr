using Nostr.Sdk;
using Tomlyn;
using Sip2Nostr.Shared;

namespace Sip2Nostr.Config;

public static class ConfigLoader
{
    public static AppConfig Load(string path)
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationException($"Config file '{path}' could not be read: {exception.Message}", exception);
        }

        if (config is null)
        {
            throw new ConfigurationException($"Config file '{path}' deserialized to an empty document.");
        }

        config.ConfigDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
        Validate(config);
        return config;
    }

    // See docs/voicemail.md for why these fail fast here.
    private static void Validate(AppConfig config)
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

        // bridge_nsec/target_npub/relays/dm_relays are never read at all
        // when Nostr is disabled - NosCallSink/VoicemailSender aren't
        // constructed, and Program.cs skips logging the bridge's npub too
        // (see there) - so validating their format then would block the
        // zero-Nostr local test path (docs/receiving-calls.md) over values
        // that are never used.
        if (config.Nostr.Enabled)
        {
            ValidateBridgeIdentity(config);
            ValidateTargetAndRelays(config);
        }

        // Same reasoning as above, the other way around: [[lines]].sound
        // only matters to LocalTestAudioSink, which only exists when Nostr
        // is disabled; [voicemail].greeting_sound only matters to
        // VoicemailSink, which is only wired in when both Nostr and
        // voicemail are enabled (see Program.cs).
        if (!config.Nostr.Enabled)
        {
            ValidateLineSoundFiles(config);
        }

        if (config.Nostr.Enabled && config.Voicemail.Enabled)
        {
            ValidateGreetingSoundFile(config);
        }
    }

    // Previously only parsed inside NostrSignalingClient, which
    // NosCallSink constructs per-call (and, before that, as a side effect
    // of Program.cs logging the bridge's npub) - so a malformed
    // bridge_nsec went unnoticed until the first real inbound call. There's
    // no way to fix a bad key by waiting, unlike a relay being briefly
    // unreachable, so this fails fast here instead. See issue #26.
    private static void ValidateBridgeIdentity(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Nostr.BridgeNsec))
        {
            throw new ConfigurationException("[nostr].bridge_nsec must not be empty when [nostr].enabled = true.");
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
    }

    private static void ValidateTargetAndRelays(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Nostr.TargetNpub))
        {
            throw new ConfigurationException("[nostr].target_npub must not be empty when [nostr].enabled = true.");
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

        // config.Nostr.Relays is never null - it defaults to [] rather
        // than being required, so a config that leaves it out entirely
        // (nostr disabled) doesn't have to provide one - but an empty
        // list still needs to be rejected here, when Nostr is enabled.
        if (config.Nostr.Relays.Count == 0)
        {
            throw new ConfigurationException("[nostr].relays must contain at least one relay URL when [nostr].enabled = true.");
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

    // [[lines]].sound used to be resolved lazily, the first time
    // LocalTestAudioSink actually needed to play the file - a bad
    // path/format/decode failure just fell back to a sine-wave tone,
    // logged at Warning, discoverable only by placing a call.
    // SoundFileResolver.TryResolve embeds the specific reason (missing
    // file, unsupported format, decode failure) directly in the exception
    // below, rather than relying on it having been logged separately - see
    // docs/sound-files.md.
    private static void ValidateLineSoundFiles(AppConfig config)
    {
        foreach (var line in config.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Sound))
            {
                continue;
            }

            var (_, failureReason) = SoundFileResolver.TryResolve(line.Sound, config.ConfigDirectory);
            if (failureReason is not null)
            {
                throw new ConfigurationException($"[[lines]] \"{line.Label}\" sound \"{line.Sound}\": {failureReason}");
            }
        }
    }

    private static void ValidateGreetingSoundFile(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Voicemail.GreetingSound))
        {
            return;
        }

        var (_, failureReason) = SoundFileResolver.TryResolve(config.Voicemail.GreetingSound, config.ConfigDirectory);
        if (failureReason is not null)
        {
            throw new ConfigurationException($"[voicemail].greeting_sound \"{config.Voicemail.GreetingSound}\": {failureReason}");
        }
    }
}
