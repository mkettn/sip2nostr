using System.Linq;
using System.Net;
using DnsClient;
using Sip2Nostr.Config;

namespace Sip2Nostr.Dns;

// Required feature (README): SIP hostname resolution must not rely on
// System.Net.Dns / the OS resolver. This wraps a DnsClient.NET LookupClient
// configured from [dns] in config.toml, with a fallback nameserver and a
// system-resolver fallback only when [dns] is absent entirely.
public sealed class ConfiguredDnsResolver
{
    private readonly LookupClient? _lookupClient;

    public ConfiguredDnsResolver(DnsConfig? config)
    {
        if (config is null)
        {
            _lookupClient = null;
            return;
        }

        var nameServers = config.Resolvers.Select(ParseNameServer).ToArray();
        _lookupClient = new LookupClient(new LookupClientOptions(nameServers)
        {
            Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs),
        });
    }

    public async Task<IPAddress> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return literal;
        }

        if (_lookupClient is null)
        {
            var systemResult = await System.Net.Dns.GetHostAddressesAsync(host, ct);
            return systemResult.First();
        }

        var response = await _lookupClient.QueryAsync(host, QueryType.A, cancellationToken: ct);
        var address = response.Answers.ARecords().FirstOrDefault()?.Address;
        if (address is null)
        {
            throw new DnsResolutionException($"No A record found for '{host}' via configured resolver(s).");
        }

        return address;
    }

    private static NameServer ParseNameServer(string hostPort)
    {
        var parts = hostPort.Split(':', 2);
        var ip = IPAddress.Parse(parts[0]);
        var port = parts.Length == 2 ? int.Parse(parts[1]) : 53;
        return new NameServer(ip, port);
    }
}

public sealed class DnsResolutionException(string message) : Exception(message);
