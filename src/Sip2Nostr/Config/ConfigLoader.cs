using Nostr.Sdk;
using Serilog.Events;
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
        if (config.WebRtc.ConnectionLossGraceSeconds <= 0)
        {
            throw new ConfigurationException(
                "[webrtc].connection_loss_grace_seconds must be greater than 0, got " +
                $"{config.WebRtc.ConnectionLossGraceSeconds}.");
        }

        // Checked against the real Serilog level names, not just "is it
        // non-empty" - so a typo (e.g. "warn" instead of "warning") fails
        // loudly at startup instead of Program.cs's own parsing silently
        // falling back to "warning" and the operator never noticing their
        // setting was ignored. Deliberately not Enum.TryParse: it also
        // accepts the string form of any integer in LogEventLevel's
        // underlying byte range, including both a defined member's own
        // ordinal (e.g. "3", Warning) and one with no matching member at
        // all (e.g. "99") - the latter would otherwise silently produce a
        // bridge that logs nothing, ever (Serilog filters on
        // level >= minimum, and (LogEventLevel)99 is above every real
        // level, Fatal included). Comparing against the enum's own member
        // names directly closes the numeric-input loophole entirely rather
        // than only its out-of-range half (Enum.IsDefined alone still
        // accepts "3").
        if (!Enum.GetNames<LogEventLevel>().Contains(config.Logging.Level, StringComparer.OrdinalIgnoreCase))
        {
            throw new ConfigurationException(
                $"[logging].level \"{config.Logging.Level}\" is not a valid Serilog level - use one of " +
                "\"verbose\", \"debug\", \"information\", \"warning\", \"error\", or \"fatal\".");
        }

        // Every check in this block names a [voicemail] (or
        // [voicemail.transcription]/[voicemail.blossom]) setting that's
        // only ever read when voicemail itself is on: ring_timeout_seconds
        // and max_recording_seconds by NosCallSink/VoicemailSink,
        // opus_resampler_quality and recording_filename by
        // VoicemailSink.SaveRecordingAsync, delivery by
        // Program.CreateVoicemailDeliveryBackend, and
        // transcription/blossom by whichever delivery backend that
        // resolves to - none of them constructed with voicemail disabled.
        // Gated the same way ValidateBridgeIdentity/ValidateTargetAndRelays
        // below are gated on [nostr].enabled, so a stale or half-filled-in
        // value in a disabled feature's section never blocks startup - only
        // a value that's live and broken does.
        if (config.Voicemail.Enabled)
        {
            if (config.Voicemail.RingTimeoutSeconds <= 0)
            {
                throw new ConfigurationException(
                    $"[voicemail].ring_timeout_seconds must be greater than 0, got {config.Voicemail.RingTimeoutSeconds}.");
            }

            if (config.Voicemail.MaxRecordingSeconds is <= 0 or > VoicemailBudget.MaxRecordingSecondsCeiling)
            {
                throw new ConfigurationException(
                    "[voicemail].max_recording_seconds must be greater than 0 and at most " +
                    $"{VoicemailBudget.MaxRecordingSecondsCeiling}, got {config.Voicemail.MaxRecordingSeconds}.");
            }

            if (config.Voicemail.OpusResamplerQuality is < 0 or > 10)
            {
                throw new ConfigurationException(
                    $"[voicemail].opus_resampler_quality must be between 0 and 10, got {config.Voicemail.OpusResamplerQuality}.");
            }

            if (config.Voicemail.Delivery is not ("file" or "audio" or "text"))
            {
                throw new ConfigurationException(
                    "[voicemail].delivery must be \"file\", \"audio\", or \"text\", got " +
                    $"\"{config.Voicemail.Delivery}\".");
            }

            if (string.IsNullOrWhiteSpace(config.Voicemail.RecordingFilename))
            {
                throw new ConfigurationException("[voicemail].recording_filename must not be empty.");
            }

            // Without {timestamp} or {call_id}, every recording would
            // resolve to the same filename and silently overwrite the last
            // one.
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

            // Whether transcription/Blossom is actually usable for the
            // selected delivery mode is a Program.cs concern (it logs a
            // warning and falls back to local-only delivery instead - see
            // docs/voicemail.md), not something ConfigLoader fails startup
            // over: leaving a mode's requirement unconfigured is a valid
            // choice, not a mistake. What ConfigLoader still rejects here is
            // a value that *is* present but broken - that's always a typo
            // the operator should fix immediately, delivery mode
            // notwithstanding.
            if (config.Voicemail.Transcription.Engine != "whisper")
            {
                throw new ConfigurationException(
                    $"[voicemail.transcription].engine \"{config.Voicemail.Transcription.Engine}\" is not supported - only \"whisper\" is available today.");
            }

            if (!string.IsNullOrWhiteSpace(config.Voicemail.Transcription.ModelPath))
            {
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

            foreach (var server in config.Voicemail.Blossom.Servers)
            {
                if (!Uri.TryCreate(server, UriKind.Absolute, out var serverUri) ||
                    (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new ConfigurationException(
                        $"[voicemail.blossom].servers entry \"{server}\" is not a valid absolute http(s) URL.");
                }
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
