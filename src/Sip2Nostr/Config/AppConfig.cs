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
