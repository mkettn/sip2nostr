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
}

// Required feature (README): SIP hostname resolution must go through this
// configurable resolver instead of the OS resolver. Falls back to the
// system resolver only when [dns] is absent from config.toml.
public sealed class DnsConfig
{
    [property: TomlPropertyName("resolver")]
    [property: TomlRequired]
    public required string Resolver { get; init; }

    [property: TomlPropertyName("resolver_fallback")]
    public string? ResolverFallback { get; init; }

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
    [property: TomlPropertyName("run_file")]
    public string? RunFile { get; init; }
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

    // "audio" (default) uploads an encrypted copy of the recording to a
    // Blossom server and sends a file message with the link - requires
    // [voicemail.blossom] below; "text" sends a transcript instead -
    // requires [voicemail.transcription] below. If the selected mode's
    // requirement isn't configured, Program.cs logs a startup warning and
    // falls back to saving the recording locally only, with a plain-text
    // notice instead of the recording itself - see docs/voicemail.md.
    [property: TomlPropertyName("delivery")]
    public string Delivery { get; init; } = "audio";

    [property: TomlPropertyName("transcription")]
    public TranscriptionConfig Transcription { get; init; } = new();

    [property: TomlPropertyName("blossom")]
    public BlossomConfig Blossom { get; init; } = new();
}

// Only consulted when [voicemail].delivery = "text" - see docs/voicemail.md.
public sealed class TranscriptionConfig
{
    // The only engine today; the interface behind it
    // (Voicemail/IVoicemailTranscriber.cs) is built to take more.
    [property: TomlPropertyName("engine")]
    public string Engine { get; init; } = "whisper";

    // Path to a GGML model file (e.g. downloaded via whisper.cpp's
    // models/download-ggml-model.sh) - the "whisper" engine needs one to
    // do anything. Left unset isn't itself an error: Program.cs logs a
    // warning and falls back to local-only delivery instead (see
    // docs/voicemail.md).
    [property: TomlPropertyName("model_path")]
    public string? ModelPath { get; init; }

    // Optional. An ISO 639-1 code (e.g. "en"); unset auto-detects the
    // spoken language per recording, at some accuracy/latency cost.
    [property: TomlPropertyName("language")]
    public string? Language { get; init; }
}

// Consulted when [voicemail].delivery = "audio" (the default), and also
// when delivery = "text" and transcription produces nothing (see
// Voicemail/TranscribedTextDeliveryBackend.cs) - see docs/voicemail.md.
public sealed class BlossomConfig
{
    // Blossom (BUD-01/BUD-02) server base URLs, tried in order until one
    // accepts the upload. Every entry present must be an absolute
    // http(s) URL - ConfigLoader rejects a malformed one at startup - but
    // an empty list isn't itself an error: Program.cs logs a warning and
    // falls back to local-only delivery instead (see docs/voicemail.md).
    [property: TomlPropertyName("servers")]
    public List<string> Servers { get; init; } = [];
}
