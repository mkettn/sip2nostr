namespace Sip2Nostr.Voicemail;

// A parsed [voicemail].blossom_servers entry. Normally a plain http(s)
// base URL; a "unix:<absolute path>" entry instead names a Unix domain
// socket a Blossom server listens on locally. RequestUri is then a fixed
// placeholder - Blossom only cares about the URL path it's given, and a
// socket connection has no real host to put there - while SocketPath
// carries the actual destination AudioDeliveryBackend dials. ConfigLoader
// has already validated the raw string by the time Parse runs.
public sealed record BlossomServer(Uri RequestUri, string? SocketPath)
{
    private const string UnixPrefix = "unix:";

    public static BlossomServer Parse(string value) =>
        value.StartsWith(UnixPrefix, StringComparison.Ordinal)
            ? new BlossomServer(new Uri("http://blossom.local"), value[UnixPrefix.Length..])
            : new BlossomServer(new Uri(value), null);

    public override string ToString() => SocketPath is null ? RequestUri.ToString() : UnixPrefix + SocketPath;
}
