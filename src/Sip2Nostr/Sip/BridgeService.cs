using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Serilog;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using Sip2Nostr.CallerList;
using Sip2Nostr.Config;
using Sip2Nostr.Dns;
using Sip2Nostr.Signaling;
using Sip2Nostr.Voicemail;

namespace Sip2Nostr.Sip;

// Registers to the VoIP provider and dispatches inbound calls to CallBridge.
// One REGISTER for the whole account (per README: [sip] carries a single
// set of credentials for the trunk); [[lines]] are the DIDs that can ring
// on it. In the MVP every line rings the same target_npub (no per-line
// routing yet), so the matched line is only used for logging here.
public sealed class BridgeService(AppConfig config, ILogger logger) : IAsyncDisposable
{
    private const int RegistrationExpirySeconds = 3600;
    private const int RegistrationAttemptTimeoutSeconds = 20;
    private const int MaxRegisterAttemptsBeforeTemporaryFailure = 3;

    private readonly ConfiguredDnsResolver _dns = new(config.Dns);
    private readonly SIPTransport _sipTransport = new();
    private CallBridge? _callBridge;
    private VoicemailSender? _voicemailSender;
    private SIPRegistrationUserAgent? _registration;
    private SIPUserAgent? _userAgent;
    private bool _registerRequestSent;
    private bool _registerResponseReceived;
    private bool _hasLoggedOperational;
    private string? _contactHost;
    private readonly ConcurrentDictionary<string, byte> _loggedInviteCallIds = new();

    public async Task StartAsync(CancellationToken ct)
    {
        logger.Information("Starting SIP transport on UDP {ListenEndpoint}.", "0.0.0.0:5060");
        InstallSipTraceLogging();

        SIPUDPChannel sipChannel;
        try
        {
            sipChannel = new SIPUDPChannel(IPAddress.Any, 5060);
        }
        catch (ApplicationException exception) when (exception.Message.Contains("Unable to bind socket"))
        {
            throw new InvalidOperationException(
                "Could not bind UDP port 5060 - it's likely already in use by another process " +
                "(a previous sip2nostr run that didn't exit cleanly, or another SIP application " +
                "such as a softphone still registered to the provider). Free the port and try again.",
                exception);
        }

        _sipTransport.AddSIPChannel(sipChannel);

        logger.Information("Resolving SIP provider host {ProviderHost}.", config.Sip.ProviderHost);
        var providerIp = await _dns.ResolveAsync(config.Sip.ProviderHost, ct);
        var providerEndpoint = new SIPEndPoint(SIPProtocolsEnum.udp, providerIp, 5060);
        var localMediaAddress = GetLocalAddressFor(providerIp);
        _contactHost = string.IsNullOrWhiteSpace(config.Sip.ContactHost)
            ? localMediaAddress.ToString()
            : config.Sip.ContactHost;
        logger.Information("Using SIP Contact host {ContactHost}.", _contactHost);
        var registrar = $"{config.Sip.ProviderHost}:5060";
        logger.Information(
            "Resolved SIP provider {ProviderHost} to {ProviderEndpoint}; local media address is {LocalMediaAddress}; registrar URI host remains {Registrar}.",
            config.Sip.ProviderHost,
            providerEndpoint,
            localMediaAddress,
            registrar);
        InstallSipUriResolver(providerEndpoint);
        var callerListGate = new CallerListGate(
            [new ConfigCallerListProvider(config.CallerList, logger.ForContext<ConfigCallerListProvider>())],
            logger.ForContext<CallerListGate>());
        _voicemailSender = new VoicemailSender(config.Nostr, config.Voicemail, logger.ForContext<VoicemailSender>());
        _callBridge = new CallBridge(
            config.WebRtc,
            config.Nostr,
            config.Voicemail,
            _voicemailSender,
            config.ConfigDirectory,
            localMediaAddress,
            config.Sip.RtpPort,
            callerListGate,
            logger.ForContext<CallBridge>());

        if (config.Nostr.Enabled)
        {
            _ = CheckNostrConnectivitySafeAsync();
        }

        _userAgent = new SIPUserAgent(_sipTransport, null, true, null);
        _userAgent.OnIncomingCall += (ua, req) => HandleIncomingCall(ua, req, ct);
        _userAgent.OnTransactionStateChange += transaction =>
        {
            logger.Information(
                "SIP transaction state changed: {TransactionId}; method: {Method}; state: {State}.",
                transaction.TransactionId,
                transaction.TransactionRequest?.Method,
                transaction.TransactionState);
        };
        _userAgent.OnTransactionTraceMessage += (transaction, message) =>
        {
            logger.Information(
                "SIP transaction trace: {TransactionId}; method: {Method}; {Message}",
                transaction.TransactionId,
                transaction.TransactionRequest?.Method,
                message);
        };
        _userAgent.ServerCallCancelled += (_, cancelRequest) =>
        {
            logger.Information(
                "SIP server call cancelled by remote party; call-id: {CallId}; reason: {Reason}.",
                cancelRequest.Header.CallId,
                cancelRequest.Header.Reason);
        };
        _userAgent.ServerCallRingTimeout += _ =>
        {
            logger.Warning("SIP server call ring timeout.");
        };
        logger.Information("SIP user agent is ready for inbound INVITE requests.");

        _registration = new SIPRegistrationUserAgent(
            _sipTransport,
            config.Sip.Username,
            config.Sip.Password,
            registrar,
            expiry: RegistrationExpirySeconds,
            maxRegistrationAttemptTimeout: RegistrationAttemptTimeoutSeconds,
            registerFailureRetryInterval: 30,
            maxRegisterAttempts: MaxRegisterAttemptsBeforeTemporaryFailure,
            exitOnUnequivocalFailure: false,
            sendUsernameInContactHeader: true);
        _registration.OverrideAllowHeader("INVITE,ACK,BYE,CANCEL,OPTIONS,PRACK,REFER,NOTIFY,SUBSCRIBE,INFO");
        _registration.RegistrationSuccessful += (uri, response) =>
        {
            logger.Information(
                "SIP registration succeeded for {RegistrationUri} with {StatusCode} {ReasonPhrase}.",
                uri,
                GetStatusCode(response),
                GetReasonPhrase(response));

            if (!_hasLoggedOperational)
            {
                _hasLoggedOperational = true;
                logger.Information("SIP connected.");
                logger.Information("sip2nostr operational.");
            }
        };
        _registration.RegistrationTemporaryFailure += (uri, response, error) =>
        {
            logger.Warning(
                "SIP registration temporary failure for {RegistrationUri}: {StatusCode} {ReasonPhrase}; {Error}. Will retry.",
                uri,
                GetStatusCode(response),
                GetReasonPhrase(response),
                error);
        };
        _registration.RegistrationFailed += (uri, response, error) =>
        {
            logger.Error(
                "SIP registration failed for {RegistrationUri}: {StatusCode} {ReasonPhrase}; {Error}.",
                uri,
                GetStatusCode(response),
                GetReasonPhrase(response),
                error);
        };
        _registration.RegistrationRemoved += (uri, response) =>
        {
            logger.Information(
                "SIP registration removed for {RegistrationUri} with {StatusCode} {ReasonPhrase}.",
                uri,
                GetStatusCode(response),
                GetReasonPhrase(response));
        };

        logger.Information(
            "Starting SIP registration as user {SipUsername} against {Registrar}; resolved endpoint: {ProviderEndpoint}; expiry: {RegistrationExpirySeconds}s; attempt timeout: {RegistrationAttemptTimeoutSeconds}s; max attempts before temporary failure: {MaxRegisterAttempts}.",
            config.Sip.Username,
            registrar,
            providerEndpoint,
            RegistrationExpirySeconds,
            RegistrationAttemptTimeoutSeconds,
            MaxRegisterAttemptsBeforeTemporaryFailure);
        _registration.Start();
        logger.Information("SIP registration agent started.");
        _ = MonitorRegistrationStartupAsync(_registration, ct);
    }

    private void HandleIncomingCall(SIPUserAgent ua, SIPRequest inviteRequest, CancellationToken ct)
    {
        var matchedLine = config.Lines.FirstOrDefault(l =>
            SIPURI.TryParse(l.Uri, out var lineUri) && lineUri.User == inviteRequest.URI.User);
        var label = matchedLine?.Label ?? "(unmatched line)";
        logger.Information(
            "Incoming SIP call for {RequestUri}; line: {LineLabel}; from: {FromUri}.",
            inviteRequest.URI,
            label,
            inviteRequest.Header.From?.FromURI);

        _ = HandleIncomingCallSafeAsync(ua, inviteRequest, matchedLine, ct);
    }

    private async Task HandleIncomingCallSafeAsync(
        SIPUserAgent ua,
        SIPRequest inviteRequest,
        LineConfig? matchedLine,
        CancellationToken ct)
    {
        try
        {
            await _callBridge!.HandleIncomingCallAsync(ua, inviteRequest, matchedLine, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.Information("Incoming call handling stopped because shutdown was requested.");
        }
        catch (Exception exception)
        {
            logger.Error(exception, "Incoming call handling failed for {RequestUri}.", inviteRequest.URI);
        }
    }

    // Runs at startup, in parallel with SIP registration, so relay
    // reachability is known up front instead of only surfacing when the
    // first call tries to publish. Failures here are diagnostic only -
    // NostrSignalingClient.ConnectAsync connects fresh per call regardless.
    private async Task CheckNostrConnectivitySafeAsync()
    {
        try
        {
            await NostrSignalingClient.CheckConnectivityAsync(config.Nostr, logger.ForContext<NostrSignalingClient>());
        }
        catch (Exception exception)
        {
            logger.Warning(exception, "Nostr startup connectivity check failed unexpectedly.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        logger.Information("Stopping SIP registration and transport; sending zero-expiry REGISTER to remove the binding.");
        _registration?.Stop(sendZeroExpiryRegister: true);
        _userAgent?.Close();
        _sipTransport.Shutdown();

        if (_voicemailSender is not null)
        {
            await _voicemailSender.DisposeAsync();
        }
    }

    private void InstallSipTraceLogging()
    {
        _sipTransport.CustomiseRequestHeader = (_, _, request) =>
        {
            if (request.Method == SIPMethodsEnum.REGISTER)
            {
                request.Header.Contact = SIPContactHeader.ParseContactHeader(
                    $"<sip:{config.Sip.Username}@{_contactHost}>;expires={RegistrationExpirySeconds}");
                request.Header.Allow = "INVITE,ACK,BYE,CANCEL,OPTIONS,PRACK,REFER,NOTIFY,SUBSCRIBE,INFO,MESSAGE";
                request.Header.UserAgent = "Twinkle/1.10.2";
            }

            return null;
        };

        _sipTransport.CustomiseResponseHeader = (local, remote, response) =>
        {
            if (response.Header.CSeqMethod == SIPMethodsEnum.INVITE)
            {
                if ((int)response.StatusCode >= 180)
                {
                    response.Header.Contact = SIPContactHeader.ParseContactHeader(
                        $"<sip:{config.Sip.Username}@{_contactHost}>");
                }

                if (response.StatusCode == 200)
                {
                    response.Header.Allow = "INVITE,ACK,BYE,CANCEL,OPTIONS,PRACK,REFER,NOTIFY,SUBSCRIBE,INFO,MESSAGE";
                    response.Header.Supported = "replaces,norefersub";
                    response.Header.Server = "Twinkle/1.10.2";
                    response.Header.ContentLength = response.Body?.Length ?? 0;
                }
            }

            logger.Information(
                "Preparing SIP response {StatusCode} {ReasonPhrase} for {CSeqMethod} from {LocalEndpoint} to {RemoteEndpoint}; call-id: {CallId}; contact: {Contact}; content-length: {ContentLength}; body length: {BodyLength}.",
                (int)response.StatusCode,
                response.ReasonPhrase,
                response.Header.CSeqMethod,
                local,
                remote,
                response.Header.CallId,
                FormatContacts(response.Header.Contact),
                response.Header.ContentLength,
                response.Body?.Length ?? 0);

            if (response.Header.CSeqMethod == SIPMethodsEnum.INVITE &&
                response.StatusCode == 200)
            {
                logger.Information("Prepared outbound SIP INVITE final response:\n{SipResponse}", response.ToString().Trim());
            }

            return null;
        };

        _sipTransport.SIPRequestInTraceEvent += (local, remote, request) =>
        {
            var isInviteRetransmit = request.Method == SIPMethodsEnum.INVITE &&
                !_loggedInviteCallIds.TryAdd(request.Header.CallId, 0);
            if (isInviteRetransmit)
            {
                logger.Debug(
                    "Retransmitted SIP INVITE in for {Uri} from {RemoteEndpoint} to {LocalEndpoint}; call-id: {CallId}.",
                    request.URI,
                    remote,
                    local,
                    request.Header.CallId);
                return;
            }

            logger.Information(
                "SIP request in {Method} {Uri} from {RemoteEndpoint} to {LocalEndpoint}; call-id: {CallId}; from: {FromUri}; to: {ToUri}; contact: {Contact}; via: {Via}.",
                request.Method,
                request.URI,
                remote,
                local,
                request.Header.CallId,
                request.Header.From?.FromURI,
                request.Header.To?.ToURI,
                FormatContacts(request.Header.Contact),
                request.Header.Vias?.TopViaHeader?.ToString() ?? "(none)");

            if (request.Method == SIPMethodsEnum.INVITE)
            {
                logger.Information("Incoming first INVITE raw SIP message:\n{SipRequest}", request.ToString().Trim());
            }
        };

        _sipTransport.SIPRequestOutTraceEvent += (local, remote, request) =>
        {
            if (request.Method == SIPMethodsEnum.REGISTER)
            {
                _registerRequestSent = true;
            }

            logger.Information(
                "SIP request out {Method} {Uri} from {LocalEndpoint} to {RemoteEndpoint}; call-id: {CallId}; contact: {Contact}; auth headers: {AuthHeaderCount}.",
                request.Method,
                request.URI,
                local,
                remote,
                request.Header.CallId,
                FormatContacts(request.Header.Contact),
                request.Header.AuthenticationHeaders.Count);
        };

        _sipTransport.SIPResponseInTraceEvent += (local, remote, response) =>
        {
            if (response.Header.CSeqMethod == SIPMethodsEnum.REGISTER)
            {
                _registerResponseReceived = true;
            }

            logger.Information(
                "SIP response in {StatusCode} {ReasonPhrase} for {CSeqMethod} from {RemoteEndpoint} to {LocalEndpoint}; call-id: {CallId}; contact: {Contact}; auth headers: {AuthHeaderCount}.",
                (int)response.StatusCode,
                response.ReasonPhrase,
                response.Header.CSeqMethod,
                remote,
                local,
                response.Header.CallId,
                FormatContacts(response.Header.Contact),
                response.Header.AuthenticationHeaders.Count);
        };

        _sipTransport.SIPResponseOutTraceEvent += (local, remote, response) =>
        {
            logger.Information(
                "SIP response out {StatusCode} {ReasonPhrase} from {LocalEndpoint} to {RemoteEndpoint}; call-id: {CallId}; contact: {Contact}; content-length: {ContentLength}; body length: {BodyLength}.",
                (int)response.StatusCode,
                response.ReasonPhrase,
                local,
                remote,
                response.Header.CallId,
                FormatContacts(response.Header.Contact),
                response.Header.ContentLength,
                response.Body?.Length ?? 0);

            if (response.Header.CSeqMethod == SIPMethodsEnum.INVITE &&
                response.StatusCode == 200)
            {
                logger.Information("SIP response out raw INVITE final response:\n{SipResponse}", response.ToString().Trim());
            }
        };

        _sipTransport.SIPResponseRetransmitTraceEvent += (transaction, response, retransmit) =>
        {
            if (retransmit <= 2 || retransmit % 5 == 0)
            {
                logger.Information(
                    "SIP response retransmit {Retransmit} for {CSeqMethod} {StatusCode} {ReasonPhrase}; transaction: {TransactionId}; call-id: {CallId}; contact: {Contact}; content-length: {ContentLength}; body length: {BodyLength}.",
                    retransmit,
                    response.Header.CSeqMethod,
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    transaction.TransactionId,
                    response.Header.CallId,
                    FormatContacts(response.Header.Contact),
                    response.Header.ContentLength,
                    response.Body?.Length ?? 0);
            }
            else
            {
                logger.Debug(
                    "SIP response retransmit {Retransmit} for {CSeqMethod} {StatusCode} {ReasonPhrase}; transaction: {TransactionId}; call-id: {CallId}.",
                    retransmit,
                    response.Header.CSeqMethod,
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    transaction.TransactionId,
                    response.Header.CallId);
            }

            if (response.Header.CSeqMethod == SIPMethodsEnum.INVITE &&
                response.StatusCode == 200)
            {
                logger.Debug("SIP response retransmit raw INVITE final response:\n{SipResponse}", response.ToString().Trim());
            }
        };

        _sipTransport.SIPBadRequestInTraceEvent += (local, remote, message, errorField, _) =>
        {
            logger.Warning(
                "Bad SIP request from {RemoteEndpoint} to {LocalEndpoint}: {Message}; field: {ErrorField}.",
                remote,
                local,
                message,
                errorField);
        };

        _sipTransport.SIPBadResponseInTraceEvent += (local, remote, message, errorField, _) =>
        {
            logger.Warning(
                "Bad SIP response from {RemoteEndpoint} to {LocalEndpoint}: {Message}; field: {ErrorField}.",
                remote,
                local,
                message,
                errorField);
        };
    }

    private void InstallSipUriResolver(SIPEndPoint providerEndpoint)
    {
        _sipTransport.ResolveSIPUriCallbackAsync = async (uri, _, cancellationToken) =>
        {
            if (IsConfiguredProvider(uri))
            {
                logger.Debug(
                    "Resolved SIP URI {SipUri} via configured resolver cache to {ProviderEndpoint}.",
                    uri,
                    providerEndpoint);
                return providerEndpoint;
            }

            var host = uri.MAddrOrHostAddress;
            var address = IPAddress.TryParse(host, out var literalAddress)
                ? literalAddress
                : await _dns.ResolveAsync(host, cancellationToken);
            var endpoint = new SIPEndPoint(uri.Protocol, address, GetSipPort(uri));
            logger.Debug(
                "Resolved SIP URI {SipUri} via configured resolver to {SipEndpoint}.",
                uri,
                endpoint);
            return endpoint;
        };

        _sipTransport.ResolveSIPUriFromCacheCallback = (uri, _) =>
        {
            if (IsConfiguredProvider(uri))
            {
                return providerEndpoint;
            }

            // Responses sent back to a caller are addressed using the literal IP
            // taken from the request's Via header (SIPTransport.SendResponseAsync
            // builds a lookup URI from Via's "received" address), never a hostname.
            // SIPSorcery's default resolver (SIPDns.ResolveFromCache) special-cases
            // this and returns immediately; our override must too, or every such
            // lookup misses this synchronous cache, falls through to the async
            // "kick off a background resolution and return SocketError.InProgress"
            // path in SIPTransport, and the response is silently never sent - the
            // call rings and answers locally but nothing ever reaches the caller.
            return IPAddress.TryParse(uri.MAddrOrHostAddress, out var literalAddress)
                ? new SIPEndPoint(uri.Protocol, literalAddress, GetSipPort(uri))
                : null;
        };
    }

    private static string FormatContacts(List<SIPContactHeader>? contacts) =>
        contacts is { Count: > 0 }
            ? string.Join(", ", contacts.Select(contact => contact.ToString()))
            : "(none)";

    private bool IsConfiguredProvider(SIPURI uri) =>
        string.Equals(uri.MAddrOrHostAddress, config.Sip.ProviderHost, StringComparison.OrdinalIgnoreCase);

    private static int GetSipPort(SIPURI uri)
    {
        var hostPort = uri.HostPort;
        var separatorIndex = hostPort.LastIndexOf(':');
        if (separatorIndex >= 0 &&
            separatorIndex < hostPort.Length - 1 &&
            int.TryParse(hostPort[(separatorIndex + 1)..], out var port))
        {
            return port;
        }

        return uri.Protocol == SIPProtocolsEnum.tls ? 5061 : 5060;
    }

    private async Task MonitorRegistrationStartupAsync(SIPRegistrationUserAgent registration, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(RegistrationAttemptTimeoutSeconds + 5), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        if (!registration.IsRegistered)
        {
            logger.Warning(
                "SIP registration is still not complete after {ElapsedSeconds}s; REGISTER sent: {RegisterRequestSent}; REGISTER response received: {RegisterResponseReceived}; last attempt at {LastRegisterAttemptAt}.",
                RegistrationAttemptTimeoutSeconds + 5,
                _registerRequestSent,
                _registerResponseReceived,
                registration.LastRegisterAttemptAt);
        }
    }

    private static int? GetStatusCode(SIPResponse? response) =>
        response is null ? null : (int)response.StatusCode;

    private static string GetReasonPhrase(SIPResponse? response) =>
        response?.ReasonPhrase ?? "(no SIP response)";

    private static IPAddress GetLocalAddressFor(IPAddress remoteAddress)
    {
        using var socket = new Socket(remoteAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(remoteAddress, 5060);
        return ((IPEndPoint)socket.LocalEndPoint!).Address;
    }
}
