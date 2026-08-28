using Tomlyn.Serialization;

namespace Sip2Nostr.Config;

public sealed class AppConfig
{
    [property: TomlIgnore]
    public string ConfigDirectory { get; set; } = Directory.GetCurrentDirectory();

    [property: TomlPropertyName("sip")]
    public required SipConfig Sip { get; init; }

    [property: TomlPropertyName("dns")]
    public DnsConfig? Dns { get; init; }

    [property: TomlPropertyName("lines")]
    public required List<LineConfig> Lines { get; init; }

    [property: TomlPropertyName("nostr")]
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
    public required string ProviderHost { get; init; }

    [property: TomlPropertyName("username")]
    public required string Username { get; init; }

    [property: TomlPropertyName("password")]
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
    public required string Resolver { get; init; }

    [property: TomlPropertyName("resolver_fallback")]
    public string? ResolverFallback { get; init; }

    [property: TomlPropertyName("timeout_ms")]
    public int TimeoutMs { get; init; } = 2000;
}

public sealed class LineConfig
{
    [property: TomlPropertyName("uri")]
    public required string Uri { get; init; }

    [property: TomlPropertyName("label")]
    public required string Label { get; init; }

    [property: TomlPropertyName("sound")]
    public string? Sound { get; init; }
}

public sealed class NostrConfig
{
    [property: TomlPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [property: TomlPropertyName("relays")]
    public required List<string> Relays { get; init; }

    [property: TomlPropertyName("bridge_nsec")]
    public required string BridgeNsec { get; init; }

    [property: TomlPropertyName("target_npub")]
    public required string TargetNpub { get; init; }
}

public sealed class WebRtcConfig
{
    [property: TomlPropertyName("stun_servers")]
    public List<string> StunServers { get; init; } = [];

    [property: TomlPropertyName("turn_server")]
    public string? TurnServer { get; init; }
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

    // See docs/voicemail.md - ConfigLoader rejects anything larger.
    [property: TomlPropertyName("max_recording_seconds")]
    public int MaxRecordingSeconds { get; init; } = Sip2Nostr.Shared.VoicemailBudget.MaxRecordingSeconds;

    // Optional. Same format rules as [[lines]].sound: raw 8 kHz 16-bit PCM
    // works directly, mono Ogg/Opus (.ogg/.opus) is decoded in-process.
    // Falls back to a short tone if unset.
    [property: TomlPropertyName("greeting_sound")]
    public string? GreetingSound { get; init; }

    // Relative to the config file's directory unless rooted.
    [property: TomlPropertyName("recordings_dir")]
    public string RecordingsDir { get; init; } = "voicemail";

    // Optional. Relays to publish the voicemail NIP-17 DM to, if different
    // from [nostr].relays (e.g. target_npub advertises a separate NIP-17
    // kind:10050 DM inbox relay list). Falls back to Nostr.Sdk's default
    // NIP-17 relay resolution against [nostr].relays if unset/empty.
    [property: TomlPropertyName("dm_relays")]
    public List<string> DmRelays { get; init; } = [];

    // "audio" inlines Opus/OGG (default); "text" sends a transcript
    // instead - see [voicemail.transcription] and docs/voicemail.md.
    [property: TomlPropertyName("delivery")]
    public string Delivery { get; init; } = "audio";

    [property: TomlPropertyName("transcription")]
    public TranscriptionConfig Transcription { get; init; } = new();
}

// Only consulted when [voicemail].delivery = "text" - see docs/voicemail.md.
public sealed class TranscriptionConfig
{
    // The only engine today; the interface behind it
    // (Voicemail/IVoicemailTranscriber.cs) is built to take more.
    [property: TomlPropertyName("engine")]
    public string Engine { get; init; } = "whisper";

    // Path to a GGML model file (e.g. downloaded via whisper.cpp's
    // models/download-ggml-model.sh) - required for the "whisper" engine.
    [property: TomlPropertyName("model_path")]
    public string? ModelPath { get; init; }

    // Optional. An ISO 639-1 code (e.g. "en"); unset auto-detects the
    // spoken language per recording, at some accuracy/latency cost.
    [property: TomlPropertyName("language")]
    public string? Language { get; init; }
}
