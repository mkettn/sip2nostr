using Tomlyn.Serialization;

namespace Sip2Nostr.Config;

// Deserialization target for the optional file ConfigLoader.MergeSecrets
// merges into an already-loaded AppConfig. Every field is nullable and
// optional - a secrets file only needs to set what it's overriding.
public sealed class SecretsFile
{
    [property: TomlPropertyName("sip")]
    public SecretsSipFields? Sip { get; init; }

    [property: TomlPropertyName("nostr")]
    public SecretsNostrFields? Nostr { get; init; }
}

public sealed class SecretsSipFields
{
    [property: TomlPropertyName("username")]
    public string? Username { get; init; }

    [property: TomlPropertyName("password")]
    public string? Password { get; init; }
}

public sealed class SecretsNostrFields
{
    [property: TomlPropertyName("bridge_nsec")]
    public string? BridgeNsec { get; init; }
}
