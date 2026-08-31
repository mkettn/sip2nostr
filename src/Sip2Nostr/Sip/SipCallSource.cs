using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Serilog;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using Sip2Nostr.CallerList;
using Sip2Nostr.Config;
using Sip2Nostr.Dns;
using Sip2Nostr.Hub;

namespace Sip2Nostr.Sip;

// Registers to the VoIP provider and raises OnIncomingCall for CallHub to
// route while the caller is ringing. One REGISTER for the whole account (per
// README: [sip] carries a single set of credentials for the trunk);
// [[lines]] are the DIDs that can ring on it. In the MVP every line rings
// the same target_npub (no per-line routing yet), so the matched line is
// only used for logging and for LocalTestAudioSink's per-line sound.
public sealed class SipCallSource(AppConfig config, ILogger logger) : ICallSource, IAsyncDisposable
{
    private const int RegistrationExpirySeconds = 3600;
    private const int RegistrationAttemptTimeoutSeconds = 20;
    private const int MaxRegisterAttemptsBeforeTemporaryFailure = 3;

    private static readonly SDPWellKnownMediaFormatsEnum[] PreferredAudioFormats =
    [
        SDPWellKnownMediaFormatsEnum.PCMA,
        SDPWellKnownMediaFormatsEnum.PCMU,
    ];

    private readonly ConfiguredDnsResolver _dns = new(config.Dns);
    private readonly SIPTransport _sipTransport = new();
    private readonly CallerListGate _callerListGate = new(
        [new ConfigCallerListProvider(config.CallerList, logger.ForContext<ConfigCallerListProvider>())],
        logger.ForContext<CallerListGate>());
    private SIPRegistrationUserAgent? _registration;
    private SIPUserAgent? _userAgent;
    private IPAddress _localMediaAddress = IPAddress.Any;
    private bool _registerRequestSent;
    private bool _registerResponseReceived;
    private bool _hasLoggedOperational;
    private string? _contactHost;
    private readonly ConcurrentDictionary<string, byte> _loggedInviteCallIds = new();

    public event Func<Call, Task>? OnIncomingCall;

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
        _localMediaAddress = GetLocalAddressFor(providerIp);
        _contactHost = string.IsNullOrWhiteSpace(config.Sip.ContactHost)
            ? _localMediaAddress.ToString()
            : config.Sip.ContactHost;
        logger.Information("Using SIP Contact host {ContactHost}.", _contactHost);
        var registrar = $"{config.Sip.ProviderHost}:5060";
        logger.Information(
            "Resolved SIP provider {ProviderHost} to {ProviderEndpoint}; local media address is {LocalMediaAddress}; registrar URI host remains {Registrar}.",
            config.Sip.ProviderHost,
            providerEndpoint,
            _localMediaAddress,
            registrar);
        InstallSipUriResolver(providerEndpoint);

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
            await AcceptAndRouteCallAsync(ua, inviteRequest, matchedLine, ct);
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

    // Hands a ringing SIP call to CallHub and answers it only once a sink
    // says it's ready to take it - see Call.cs for the contract and
    // docs/hub-architecture.md for why the decision lives there.
    private async Task AcceptAndRouteCallAsync(
        SIPUserAgent ua,
        SIPRequest inviteRequest,
        LineConfig? matchedLine,
        CancellationToken ct)
    {
        logger.Information("Accepting SIP call for {RequestUri}.", inviteRequest.URI);
        LogInviteSdp(inviteRequest);
        var selectedAudioFormat = SelectOfferedG711Format(inviteRequest);
        logger.Information("Selected SIP audio codec {AudioCodec} for the SDP answer.", selectedAudioFormat);

        // AcceptCall sends 100 Trying and 180 Ringing itself, so the caller
        // hears ringback from here until a sink answers.
        var uas = ua.AcceptCall(inviteRequest);
        uas.ClientTransaction.OnAckReceived += (_, _, _, ackRequest) =>
        {
            logger.Information(
                "Received SIP ACK for answered INVITE; call-id: {CallId}; request URI: {RequestUri}; to tag: {ToTag}; from tag: {FromTag}.",
                ackRequest.Header.CallId,
                ackRequest.URI,
                ackRequest.Header.To?.ToTag,
                ackRequest.Header.From?.FromTag);
            return Task.FromResult(SocketError.Success);
        };
        logger.Information(
            "Accepted SIP INVITE with local transaction tag {LocalTag}; the caller is ringing.",
            uas.ClientTransaction.LocalTag);

        var rawCallerNumber = inviteRequest.Header.From?.FromURI?.User ?? string.Empty;
        var callerNumber = PhoneNumberNormalizer.Normalize(rawCallerNumber);
        logger.Information(
            "Caller number normalized to {CallerNumber} (raw: {RawCallerNumber}).",
            callerNumber,
            rawCallerNumber);

        if (!await _callerListGate.IsAllowedAsync(callerNumber, ct))
        {
            logger.Information("Caller {CallerNumber} is not allowed to reach this line; rejecting.", callerNumber);
            uas.Reject(SIPResponseStatusCodesEnum.Forbidden, null);
            return;
        }

        var sipMediaSession = new RTPSession(false, false, false);
        sipMediaSession.addTrack(CreateAudioTrack(selectedAudioFormat, MediaStreamStatusEnum.SendRecv));

        var callId = Guid.NewGuid().ToString();
        logger.Information("SIP call ringing with call-id {CallId}.", callId);

        // Wired up before OnIncomingCall is raised (not after) so a caller
        // hangup while a sink is still working on the call (waiting for a
        // WebRTC answer, recording voicemail) is observed promptly instead
        // of only surfacing when the sink gives up on its own.
        var hangupTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCallHungup(SIPDialogue _) => hangupTcs.TrySetResult();

        // A caller who gives up while the call is still ringing CANCELs the
        // INVITE rather than sending BYE, and OnCallHungup only ever fires
        // for an established dialogue - so without this a sink would keep
        // ringing Nostr (or start recording a voicemail) for a caller
        // who's already gone.
        void OnCallCancelled(ISIPServerUserAgent _, SIPRequest __)
        {
            logger.Information("Caller {CallerNumber} cancelled call {CallId} while it was ringing.", callerNumber, callId);
            hangupTcs.TrySetResult();
        }

        // sipsorcery expires an INVITE transaction left ringing for
        // SIPTimings.MAX_RING_TIME (3 minutes); past that the call can no
        // longer be answered, so it's over as far as any sink is concerned.
        void OnNoRingTimeout(ISIPServerUserAgent _)
        {
            logger.Warning("SIP INVITE for call {CallId} timed out while ringing; it can no longer be answered.", callId);
            hangupTcs.TrySetResult();
        }

        ua.OnCallHungup += OnCallHungup;
        uas.CallCancelled += OnCallCancelled;
        uas.NoRingTimeout += OnNoRingTimeout;
        using var ctReg = ct.Register(() => hangupTcs.TrySetResult());

        var answered = false;

        async Task<bool> AnswerCallAsync()
        {
            try
            {
                if (hangupTcs.Task.IsCompleted)
                {
                    logger.Information("Not answering call {CallId}; the caller is no longer on the line.", callId);
                    return false;
                }

                logger.Information("Answering SIP call {CallId}.", callId);
                var success = await ua.Answer(uas, sipMediaSession, null, _localMediaAddress);
                LogFinalInviteResponse(uas);
                if (!success)
                {
                    logger.Warning("SIP call answer failed for call {CallId}.", callId);
                    return false;
                }

                answered = true;
                logger.Information("SIP call answered with call-id {CallId}.", callId);
                return true;
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Answering SIP call {CallId} failed.", callId);
                return false;
            }
        }

        // Lazy, not a plain call: the sink chain can offer the same call to
        // more than one sink, and each has to see the same single answer
        // outcome rather than a second 200 OK attempt.
        var answerOnce = new Lazy<Task<bool>>(AnswerCallAsync, LazyThreadSafetyMode.ExecutionAndPublication);

        var call = new Call(
            callId,
            callerNumber,
            matchedLine?.Label,
            new AudioFormat(selectedAudioFormat),
            new RtpSessionCallAudio(sipMediaSession),
            () => answerOnce.Value,
            () =>
            {
                // ua.Hangup() only sends BYE for an established dialogue, so
                // a call nothing ever answered has to be turned down on its
                // still-pending INVITE transaction instead - otherwise the
                // caller keeps ringing until the provider gives up.
                if (uas.IsUASAnswered)
                {
                    ua.Hangup();
                }
                else
                {
                    logger.Information("Nothing answered call {CallId}; turning it down.", callId);
                    uas.Reject(SIPResponseStatusCodesEnum.TemporarilyUnavailable, null);
                }

                return Task.CompletedTask;
            },
            hangupTcs.Task);

        try
        {
            // ua (the shared _userAgent) only ever raises one incoming call
            // at a time in practice, but OnIncomingCall is a public event -
            // await every subscriber explicitly rather than Invoke(), which
            // on a multicast delegate only awaits whichever Task the last
            // subscriber returned.
            if (OnIncomingCall is not null)
            {
                foreach (var handler in OnIncomingCall.GetInvocationList())
                {
                    await ((Func<Call, Task>)handler)(call);
                }
            }
        }
        finally
        {
            ua.OnCallHungup -= OnCallHungup;
            uas.CallCancelled -= OnCallCancelled;
            uas.NoRingTimeout -= OnNoRingTimeout;

            // SIPUserAgent only takes ownership of the media session it's
            // handed at answer time, so an unanswered call's session is
            // still ours to close.
            if (!answered)
            {
                sipMediaSession.Close("call was never answered");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        logger.Information("Stopping SIP registration and transport; sending zero-expiry REGISTER to remove the binding.");
        _registration?.Stop(sendZeroExpiryRegister: true);
        _userAgent?.Close();
        _sipTransport.Shutdown();
        return ValueTask.CompletedTask;
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

    private static SDPWellKnownMediaFormatsEnum SelectOfferedG711Format(SIPRequest inviteRequest)
    {
        var offeredPayloads = GetOfferedAudioPayloads(inviteRequest.Body);
        foreach (var payload in offeredPayloads)
        {
            if (payload == "8")
            {
                return SDPWellKnownMediaFormatsEnum.PCMA;
            }

            if (payload == "0")
            {
                return SDPWellKnownMediaFormatsEnum.PCMU;
            }
        }

        return PreferredAudioFormats[0];
    }

    private static IEnumerable<string> GetOfferedAudioPayloads(string? sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp))
        {
            yield break;
        }

        foreach (var line in sdp.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("m=audio ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var index = 3; index < parts.Length; index++)
            {
                yield return parts[index];
            }

            yield break;
        }
    }

    private static MediaStreamTrack CreateAudioTrack(
        SDPWellKnownMediaFormatsEnum audioFormat,
        MediaStreamStatusEnum streamStatus) =>
        new(new AudioFormat(audioFormat), streamStatus);

    private void LogFinalInviteResponse(SIPServerUserAgent uas)
    {
        var response = uas.ClientTransaction.TransactionFinalResponse;
        if (response is null)
        {
            logger.Warning("SIP INVITE transaction has no final response to log after answer attempt.");
            return;
        }

        logger.Information("Actual final SIP INVITE response:\n{SipResponse}", response.ToString().Trim());
    }

    private void LogInviteSdp(SIPRequest inviteRequest)
    {
        if (string.IsNullOrWhiteSpace(inviteRequest.Body))
        {
            logger.Warning("Incoming INVITE has no SDP body.");
            return;
        }

        logger.Information("Incoming INVITE SDP offer:\n{SdpOffer}", inviteRequest.Body.Trim());
    }
}
