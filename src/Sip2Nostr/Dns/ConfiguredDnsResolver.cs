using System.Linq;
using System.Net;
using System.Net.Sockets;
using DnsClient;
using Sip2Nostr.Config;

namespace Sip2Nostr.Dns;

// Wraps a DnsClient.NET LookupClient from [dns] in config.toml, falling
// back to the system resolver when [dns].enabled is false. See
// docs/receiving-calls.md.
public sealed class ConfiguredDnsResolver
{
    private readonly LookupClient? _lookupClient;

    public ConfiguredDnsResolver(DnsConfig config)
    {
        if (!config.Enabled)
        {
            _lookupClient = null;
            return;
        }

        // ConfigLoader.Validate already rejected any entry TryParseNameServer
        // can't parse, so a failure here would mean that check was bypassed
        // (e.g. an AppConfig built directly rather than via ConfigLoader.Load) -
        // a bug to surface loudly, not a startup-time operator mistake.
        var nameServers = config.Resolvers.Select(value =>
            TryParseNameServer(value, out var server, out var failureReason)
                ? server!
                : throw new InvalidOperationException($"Invalid [dns].resolvers entry \"{value}\": {failureReason}"))
            .ToArray();
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
            // IPv4 only, matching QueryType.A below - see
            // docs/receiving-calls.md.
            var systemResult = await System.Net.Dns.GetHostAddressesAsync(host, ct);
            var systemAddress = systemResult.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (systemAddress is null)
            {
                throw new DnsResolutionException($"No IPv4 address found for '{host}' via the system resolver.");
            }

            return systemAddress;
        }

        var response = await _lookupClient.QueryAsync(host, QueryType.A, cancellationToken: ct);
        var address = response.Answers.ARecords().FirstOrDefault()?.Address;
        if (address is null)
        {
            throw new DnsResolutionException($"No A record found for '{host}' via configured resolver(s).");
        }

        return address;
    }

    // Accepts a bare IP ("1.1.1.1", "2606:4700:4700::1111" - no port, since
    // an unbracketed IPv6 literal's own colons make a trailing ":port"
    // ambiguous), "ip:port" for IPv4, or bracketed "[ipv6]"/"[ipv6]:port"
    // for IPv6 with an explicit port. Shared between ConfigLoader.Validate
    // (which needs a clean per-entry failure reason for
    // ConfigurationException, not a crash on first use) and the
    // constructor above.
    public static bool TryParseNameServer(string value, out NameServer? server, out string? failureReason)
    {
        server = null;
        failureReason = null;

        var host = value;
        var port = 53;

        if (value.StartsWith('['))
        {
            var closeIndex = value.IndexOf(']');
            if (closeIndex < 0)
            {
                failureReason = "missing closing ']' for a bracketed IPv6 address";
                return false;
            }

            host = value[1..closeIndex];
            var remainder = value[(closeIndex + 1)..];
            if (remainder.Length > 0)
            {
                if (!remainder.StartsWith(':') || !int.TryParse(remainder[1..], out port))
                {
                    failureReason = "expected \":<port>\" after the closing ']'";
                    return false;
                }
            }
        }
        else if (!IPAddress.TryParse(host, out _))
        {
            // Not a bare IP literal (with or without unbracketed IPv6
            // colons) - see if it's "ip:port" (exactly one colon, so not
            // an unbracketed IPv6 literal, which always has more than one).
            var lastColon = value.LastIndexOf(':');
            if (lastColon >= 0 && value.IndexOf(':') == lastColon)
            {
                host = value[..lastColon];
                if (!int.TryParse(value[(lastColon + 1)..], out port))
                {
                    failureReason = $"\"{value[(lastColon + 1)..]}\" is not a valid port";
                    return false;
                }
            }
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            failureReason = $"\"{value}\" is not a valid IP address, \"ip:port\", or \"[ipv6]:port\"";
            return false;
        }

        // int.TryParse above accepts any int, but NameServer's constructor
        // builds an IPEndPoint, which throws ArgumentOutOfRangeException
        // for anything outside this range - checked here instead, so an
        // out-of-range port fails the same clean way a bad address does,
        // rather than throwing past this method uncaught.
        if (port is < 1 or > 65535)
        {
            failureReason = $"port {port} is outside 1-65535";
            return false;
        }

        server = new NameServer(ip, port);
        return true;
    }
}

public sealed class DnsResolutionException(string message) : Exception(message);
