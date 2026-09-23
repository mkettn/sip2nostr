using Tomlyn.Serialization;

namespace Sip2Nostr.Config;

// Every `required` property below also carries `[TomlRequired]` - C#'s
// `required` is a compile-time-only signal to callers who construct an
// AppConfig directly (nobody does; Tomlyn deserializes it via reflection,
// bypassing that check entirely). Without `[TomlRequired]`, a missing key
// deserializes to a silent null instead of failing, and ConfigLoader
// dereferencing it crashes with a NullReferenceException - confirmed by
// testing directly against Tomlyn 2.10.1. `[TomlRequired]` makes Tomlyn
// itself throw a clear TomlException (already caught and wrapped by
// ConfigLoader.Load) for a missing key, before any of that.
//
// `required`/`[TomlRequired]` is only for a value every config needs
// regardless of which features are turned on - [sip]/[[lines]] are the
// only genuine examples of that here. A value that's only needed when
// some other setting enables it (NostrConfig's own bridge_nsec/
// target_npub/relays, needed only when [nostr].enabled) is deliberately
// NOT required at this level - it gets a normal optional/defaulted
// property instead, with ConfigLoader.Validate enforcing the dependency
// at runtime, conditioned on the setting that creates it. Marking it
// required here instead would force it to be present even in a config
// that never uses it.
public sealed class AppConfig
{
    [property: TomlIgnore]
    public string ConfigDirectory { get; set; } = Directory.GetCurrentDirectory();

    [property: TomlPropertyName("sip")]
    [property: TomlRequired]
    public required SipConfig Sip { get; init; }

    [property: TomlPropertyName("dns")]
    public DnsConfig? Dns { get; init; }

    [property: TomlPropertyName("lines")]
    [property: TomlRequired]
    public required List<LineConfig> Lines { get; init; }

    [property: TomlPropertyName("nostr")]
    [property: TomlRequired]
    public required NostrConfig Nostr { get; init; }

    [property: TomlPropertyName("webrtc")]
    public WebRtcConfig WebRtc { get; init; } = new();

    [property: TomlPropertyName("logging")]
    public LoggingConfig Logging { get; init; } = new();

    [property: TomlPropertyName("callerlist")]
    public CallerListConfig CallerList { get; init; } = new();

    [property: TomlPropertyName("voicemail")]
    public VoicemailConfig Voicemail { get; init; } = new();
}

public sealed class SipConfig
{
    [property: TomlPropertyName("provider_host")]
    [property: TomlRequired]
    public required string ProviderHost { get; init; }

    [property: TomlPropertyName("username")]
    [property: TomlRequired]
    public required string Username { get; init; }

    [property: TomlPropertyName("password")]
    [property: TomlRequired]
    public required string Password { get; init; }

    [property: TomlPropertyName("contact_host")]
    public string? ContactHost { get; init; }

    [property: TomlPropertyName("rtp_port")]
    public int RtpPort { get; init; } = 8000;

    // Default true: SIP signaling (REGISTER/INVITE/etc.) goes out over TLS
    // (SIPS, port 5061) rather than plain UDP. SipCallSource fails startup
    // if a TLS connection to provider_host can't actually be established -
    // set this false only if the provider genuinely has no TLS/SIPS option,
    // which trades that credential/metadata exposure for the ability to
    // start at all; a startup warning is logged as a standing reminder.
    // See issue #35 and docs/propagating-to-nostr.md.
    [property: TomlPropertyName("tls")]
    public bool Tls { get; init; } = true;
}

// Required feature (README): SIP hostname resolution must go through this
// configurable resolver instead of the OS resolver. Falls back to the
// system resolver only when [dns] is absent from config.toml.
public sealed class DnsConfig
{
    // Nameservers DnsClient.NET's LookupClient may query - it picks among
    // them per request rather than always preferring the first entry, so
    // this isn't a strict primary/fallback ordering despite the old
    // resolver/resolver_fallback naming having implied one. Each entry
    // must parse as ConfiguredDnsResolver.TryParseNameServer expects
    // ("ip", "ip:port", or "[ipv6]:port") - ConfigLoader.Validate checks
    // both that and that the list isn't empty, since [dns] being present
    // at all means at least one nameserver was intended.
    [property: TomlPropertyName("resolvers")]
    [property: TomlRequired]
    public required List<string> Resolvers { get; init; }

    [property: TomlPropertyName("timeout_ms")]
    public int TimeoutMs { get; init; } = 2000;
}

public sealed class LineConfig
{
    [property: TomlPropertyName("uri")]
    [property: TomlRequired]
    public required string Uri { get; init; }

    [property: TomlPropertyName("label")]
    [property: TomlRequired]
    public required string Label { get; init; }

    [property: TomlPropertyName("sound")]
    public string? Sound { get; init; }
}

public sealed class NostrConfig
{
    [property: TomlPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    // Deliberately NOT [TomlRequired]/required, unlike [sip]/[[lines]]
    // above: these three are only needed when enabled is true (NosCallSink
    // isn't even constructed otherwise - see Program.cs), so a config that
    // disables Nostr shouldn't have to provide them at all - not even a
    // placeholder. ConfigLoader.ValidateBridgeIdentity/ValidateTargetAndRelays
    // (both gated on Enabled) are what actually enforce "required when
    // Nostr is on," including rejecting these when left at their empty
    // defaults.
    [property: TomlPropertyName("relays")]
    public List<string> Relays { get; init; } = [];

    [property: TomlPropertyName("bridge_nsec")]
    public string? BridgeNsec { get; init; }

    [property: TomlPropertyName("target_npub")]
    public string? TargetNpub { get; init; }
}

public sealed class WebRtcConfig
{
    [property: TomlPropertyName("stun_servers")]
    public List<string> StunServers { get; init; } = [];

    [property: TomlPropertyName("turn_server")]
    public string? TurnServer { get; init; }

    // How long a bridged call's WebRTC connection can sit in
    // "disconnected" before NosCallSink's ConnectionLossWatcher gives up
    // on it recovering - long enough to ride out a brief network blip,
    // short enough that a caller isn't stuck on dead air for minutes. See
    // docs/propagating-to-nostr.md.
    [property: TomlPropertyName("connection_loss_grace_seconds")]
    public int ConnectionLossGraceSeconds { get; init; } = 15;
}

public sealed class LoggingConfig
{
    // Optional: write each process run to its own log file, independent of
    // whatever the console shows - see FileLevel below for how loud that
    // file is by default.
    [property: TomlPropertyName("file")]
    public string? File { get; init; }

    // A Serilog LogEventLevel name (case-insensitive): "verbose", "debug",
    // "information", "warning", "error", or "fatal" - checked against the
    // real enum by ConfigLoader.Validate, so a typo fails at startup
    // rather than silently falling back to this default. "warning" here,
    // not Serilog's own "information" default: a bridge running
    // unattended should be quiet unless something's actually wrong: turn
    // it down to "information"/"debug" when troubleshooting. Console-only
    // - see FileLevel for the file sink's own, independent level.
    [property: TomlPropertyName("console_level")]
    public string ConsoleLevel { get; init; } = "warning";

    // The file sink's own minimum level - same Serilog level names as
    // ConsoleLevel, validated the same way, but independent of it and
    // File above only consumes it when File is actually set. Defaults to
    // "information", louder than ConsoleLevel's own "warning" default,
    // because a fresh timestamped log per run exists specifically for
    // after-the-fact troubleshooting: by the time you're reaching for it,
    // the run that misbehaved is already over, so it shouldn't come up
    // empty just because the console was left quiet at the time.
    [property: TomlPropertyName("file_level")]
    public string FileLevel { get; init; } = "information";

    // Separate from ConsoleLevel/FileLevel on purpose: those gate
    // Serilog's own log events, while this gates exactly one plain
    // Console.WriteLine - the "sip2nostr running, press Ctrl+C to exit"
    // line - that was never a log event a level could suppress in the
    // first place. A human watching a foreground terminal still gets that
    // one confirmation by default even at console_level = "warning";
    // console_quiet = true is for a supervised/scripted run (systemd, a
    // service manager) where nothing should print to stdout absent a real
    // problem.
    [property: TomlPropertyName("console_quiet")]
    public bool ConsoleQuiet { get; init; }

    // Console-only, like ConsoleQuiet above: a supervisor that already
    // timestamps captured output - systemd/journald being the common
    // case, since journald stamps every line with its own arrival time
    // regardless of what the line itself contains - ends up showing two
    // timestamps per line otherwise, its own plus this process's. The
    // file sink's timestamp is never affected by this: a log file has no
    // such external stamping to duplicate, and is the only record of when
    // something happened once the process has exited.
    [property: TomlPropertyName("console_timestamps")]
    public bool ConsoleTimestamps { get; init; } = true;
}

// Blacklist always wins on match. An empty whitelist means blacklist-only
// mode (everyone not blacklisted is allowed); a non-empty whitelist
// switches to deny-by-default (only whitelisted numbers get through).
public sealed class CallerListConfig
{
    [property: TomlPropertyName("blacklist")]
    public List<string> Blacklist { get; init; } = [];

    [property: TomlPropertyName("whitelist")]
    public List<string> Whitelist { get; init; } = [];
}

// Answering-machine fallback for when [nostr] is enabled but the callee
// never answers over Nostr within ring_timeout_seconds: the call is
// diverted to a local greeting + recording instead of ringing forever.
// Opt-in (disabled by default) - see docs/voicemail.md.
public sealed class VoicemailConfig
{
    [property: TomlPropertyName("enabled")]
    public bool Enabled { get; init; } = false;

    [property: TomlPropertyName("ring_timeout_seconds")]
    public int RingTimeoutSeconds { get; init; } = 20;

    // Neither delivery mode inlines the recording in the DM itself
    // ("text" sends a transcript, "audio" sends a Blossom upload's URL -
    // see docs/voicemail.md), so this isn't a size budget - just a
    // sanity limit on how much PCM VoicemailSink buffers in memory while
    // recording. ConfigLoader caps it at
    // VoicemailBudget.MaxRecordingSecondsCeiling so it can't itself
    // become unbounded.
    [property: TomlPropertyName("max_recording_seconds")]
    public int MaxRecordingSeconds { get; init; } = Sip2Nostr.Shared.VoicemailBudget.MaxRecordingSeconds;

    // Passed to Concentus.Oggfile.OpusOggWriteStream's resamplerQuality
    // parameter when encoding a recording - ConfigLoader rejects anything
    // outside Concentus' own 0-10 range.
    [property: TomlPropertyName("opus_resampler_quality")]
    public int OpusResamplerQuality { get; init; } = Sip2Nostr.Shared.VoicemailBudget.OpusResamplerQuality;

    // Optional. Same format rules as [[lines]].sound: raw 8 kHz 16-bit PCM
    // works directly, mono Opus (.opus) is decoded in-process. Falls back
    // to a short tone if unset.
    [property: TomlPropertyName("greeting_sound")]
    public string? GreetingSound { get; init; }

    // Relative to the config file's directory unless rooted.
    [property: TomlPropertyName("recordings_dir")]
    public string RecordingsDir { get; init; } = "voicemail";

    // Filename for a saved recording, relative to recordings_dir - joined
    // straight onto it, so must be a relative path with no ".." segment
    // (checked by ConfigLoader; a rooted value would otherwise silently
    // discard recordings_dir entirely via Path.Combine). Placeholders:
    // {timestamp} (yyyyMMdd-HHmmss), {caller} (the normalized caller
    // number), {call_id} (a per-call unique id, not a phone number). Must
    // include {timestamp} or {call_id} - also checked by ConfigLoader -
    // so recordings from different calls can't silently overwrite each
    // other.
    [property: TomlPropertyName("recording_filename")]
    public string RecordingFilename { get; init; } = "{timestamp}-{caller}.opus";

    // Optional. Relays to publish the voicemail NIP-17 DM to, if different
    // from [nostr].relays (e.g. target_npub advertises a separate NIP-17
    // kind:10050 DM inbox relay list). Falls back to Nostr.Sdk's default
    // NIP-17 relay resolution against [nostr].relays if unset/empty.
    [property: TomlPropertyName("dm_relays")]
    public List<string> DmRelays { get; init; } = [];

    // "file" (default) - no upload, no transcription: the recording is
    // just saved to recordings_dir and a plain-text notice is sent
    // instead, the zero-setup option. "audio" uploads an encrypted copy
    // of the recording to a Blossom server and sends a file message with
    // the link - requires blossom_servers below. "text" sends a
    // transcript instead - requires transcription_model_path below. If
    // "audio"/"text"'s own requirement isn't configured, Program.cs logs
    // a startup warning and falls back to "file"'s behavior instead of
    // failing to start - see docs/voicemail.md.
    [property: TomlPropertyName("delivery")]
    public string Delivery { get; init; } = "file";

    // Path to a GGML model file (e.g. downloaded via whisper.cpp's
    // models/download-ggml-model.sh), consumed by Whisper.net - the only
    // transcription engine supported (Voicemail/IVoicemailTranscriber.cs
    // is built to take more, but there's nothing to select between yet,
    // so there's no transcription_engine setting). Left unset isn't
    // itself an error: Program.cs logs a warning and falls back to
    // delivery = "file"'s behavior instead (see docs/voicemail.md).
    [property: TomlPropertyName("transcription_model_path")]
    public string? TranscriptionModelPath { get; init; }

    // Optional. An ISO 639-1 code (e.g. "en"); unset auto-detects the
    // spoken language per recording, at some accuracy/latency cost.
    [property: TomlPropertyName("transcription_language")]
    public string? TranscriptionLanguage { get; init; }

    // Consulted when delivery = "audio", and also when delivery = "text"
    // and transcription produces nothing (see
    // Voicemail/TranscribedTextDeliveryBackend.cs) - see docs/voicemail.md.
    // Blossom (BUD-01/BUD-02) server base URLs, tried in order until one
    // accepts the upload. Every entry present must be an absolute
    // http(s) URL - ConfigLoader rejects a malformed one at startup - but
    // an empty list isn't itself an error: Program.cs logs a warning and
    // falls back to delivery = "file"'s behavior instead (see
    // docs/voicemail.md).
    [property: TomlPropertyName("blossom_servers")]
    public List<string> BlossomServers { get; init; } = [];
}
