namespace Sip2Nostr.Voicemail;

// A parsed [voicemail].blossom_servers entry. Normally a plain http(s)
// base URL; a "unix:<absolute path>" entry instead names a Unix domain
// socket a server listens on locally. RequestUri is then a fixed
// placeholder - the target only cares about the URL path it's given, and
// a socket connection has no real host to put there - while SocketPath
// carries the actual destination AudioDeliveryBackend dials. ConfigLoader
// has already validated the raw string by the time Parse runs. Named
// generically (not BlossomServer) since nothing here is Blossom-specific,
// but Blossom voicemail uploads are the only thing that constructs one
// today - see AGENTS.md on not designing for hypothetical reuse.
public sealed record ServiceUri(Uri RequestUri, string? SocketPath)
{
    private const string UnixPrefix = "unix:";

    public static ServiceUri Parse(string value) =>
        value.StartsWith(UnixPrefix, StringComparison.Ordinal)
            ? new ServiceUri(new Uri("http://localhost"), value[UnixPrefix.Length..])
            : new ServiceUri(new Uri(value), null);

    public override string ToString() => SocketPath is null ? RequestUri.ToString() : UnixPrefix + SocketPath;
}
