using System.Text.Json;
using Nostr.Sdk;
using Serilog;
using Sip2Nostr.Config;

namespace Sip2Nostr.Signaling;

// Call signaling over Nostr using NIP-AC ("WebRTC Calls"), confirmed
// against NosCall's real implementation - see docs/propagating-to-nostr.md.
// Not standard NIP-59 gift wrap: the inner signaling event is signed with
// the bridge's real identity, then wrapped in a kind-21059 event that is
// NIP-44-encrypted and signed by a fresh ephemeral keypair generated per
// message (no seal layer). One instance is scoped to a single call-id.
public sealed class NostrSignalingClient : IAsyncDisposable
{
    private const string AltText = "NIP-AC signaling";
    private const string CallTypeVoice = "voice";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly Keys _bridgeKeys;
    private readonly PublicKey _targetPubkey;
    private readonly List<RelayUrl> _relays;
    private readonly string _callId;
    private readonly ILogger _logger;
    private Client? _client;
    private TaskCompletionSource<string>? _pendingAnswer;
    private Action<IceCandidatePayload>? _onIceCandidate;

    public NostrSignalingClient(NostrConfig config, string callId, ILogger logger)
    {
        _bridgeKeys = Keys.Parse(config.BridgeNsec);
        _targetPubkey = PublicKey.Parse(config.TargetNpub);
        _relays = config.Relays.Select(RelayUrl.Parse).ToList();
        _callId = callId;
        _logger = logger;
    }

    public async Task ConnectAsync()
    {
        _client = new ClientBuilder().Signer(NostrSigner.Keys(_bridgeKeys)).Build();
        var connectedCount = await ConnectAndCheckRelaysAsync(_client, _relays, _logger);
        if (connectedCount > 0)
        {
            _logger.Information(
                "Nostr signaling connected: {ConnectedCount}/{TotalCount} relay(s) reachable.",
                connectedCount,
                _relays.Count);
        }
        else
        {
            _logger.Warning(
                "Nostr signaling: none of the {TotalCount} configured relay(s) are reachable yet; " +
                "publishing may still fail. See docs/propagating-to-nostr.md.",
                _relays.Count);
        }

        _ = Task.Run(() => _client.HandleNotifications(new WrapDispatcher(this)));

        var filter = new Filter()
            .Kind(new Kind(CallSignalKinds.WrapKind))
            .Pubkey(_bridgeKeys.PublicKey())
            .Since(Timestamp.Now());
        await _client.Subscribe(filter, null);
    }

    // Startup-time diagnostic: confirms the configured relays are actually
    // reachable before any call arrives, instead of only finding out deep
    // into a live call. Uses a throwaway connection - the real per-call
    // NostrSignalingClient always connects fresh in ConnectAsync above.
    public static async Task CheckConnectivityAsync(NostrConfig config, ILogger logger)
    {
        var bridgeKeys = Keys.Parse(config.BridgeNsec);
        var relays = config.Relays.Select(RelayUrl.Parse).ToList();
        var client = new ClientBuilder().Signer(NostrSigner.Keys(bridgeKeys)).Build();

        var connectedCount = await ConnectAndCheckRelaysAsync(client, relays, logger);
        if (connectedCount > 0)
        {
            logger.Information("Nostr connected: {ConnectedCount}/{TotalCount} relay(s) reachable.", connectedCount, relays.Count);
        }
        else
        {
            logger.Warning(
                "Nostr not connected: none of the {TotalCount} configured relay(s) are reachable. " +
                "Calls will not propagate to Nostr until this is fixed - check the relay URLs and network access.",
                relays.Count);
        }

        await client.Shutdown();
        client.Dispose();
    }

    // Connect() is fire-and-forget - it kicks off each relay's connection
    // loop and returns immediately, with no guarantee any attempt even
    // started (confirmed against rust-nostr's source: sdk/src/pool/mod.rs).
    // TryConnect() actually awaits a real per-relay connection attempt
    // within the timeout and returns which relays succeeded/failed, with
    // the real underlying error (DNS/TLS/refused/etc.) per failure - not
    // just an opaque status enum.
    private static async Task<int> ConnectAndCheckRelaysAsync(Client client, List<RelayUrl> relays, ILogger logger)
    {
        foreach (var relay in relays)
        {
            await client.AddRelay(relay);
        }

        var output = await client.TryConnect(ConnectTimeout);
        foreach (var failure in output.failed)
        {
            logger.Warning("Nostr relay {RelayUrl} failed to connect: {Reason}.", failure.Key, failure.Value);
        }

        return output.success.Count;
    }

    // Content is the raw SDP offer string; call-type is required by
    // NIP-AC on offers only. sip2nostr only ever bridges audio.
    public Task<EventId> SendOfferAsync(string sdp) =>
        PublishAsync(CallSignalKinds.CallOffer, sdp, [Tag.Parse(["call-type", CallTypeVoice])]);

    public Task<EventId> SendIceCandidateAsync(IceCandidatePayload candidate) =>
        PublishAsync(CallSignalKinds.IceCandidate, JsonSerializer.Serialize(candidate));

    public Task<EventId> SendRejectAsync(string reason) =>
        PublishAsync(CallSignalKinds.Reject, reason);

    // Sends the recorded voicemail to target_npub as a standard NIP-17
    // private direct message (kind 14 rumor, sealed and gift-wrapped by
    // Client.SendPrivateMsg) rather than reusing the NIP-AC call-signaling
    // wrap above - unlike the call offer/answer, a voicemail should be
    // readable by any NIP-17-capable client, not just NosCall.
    //
    // The audio is inlined as a base64 data URI in the message content
    // rather than uploaded to a file host: sip2nostr has no media-hosting
    // dependency today. This is a real limitation - see docs/voicemail.md -
    // large recordings can exceed a relay's max event size.
    public async Task SendVoicemailAsync(byte[] audioBytes, string mimeType, int durationSeconds, string callerNumber)
    {
        var dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(audioBytes)}";
        var content =
            $"🎤 Voicemail from {callerNumber} ({durationSeconds}s) - the call wasn't answered.\n\n{dataUri}";
        var tags = new List<Tag>
        {
            Tag.Parse(["alt", "sip2nostr voicemail"]),
            Tag.Parse(["duration", durationSeconds.ToString()]),
        };

        await _client!.SendPrivateMsg(_targetPubkey, content, tags);
    }

    public Task<string> WaitForAnswerAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAnswer = tcs;
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public void OnIceCandidateReceived(Action<IceCandidatePayload> handler) => _onIceCandidate = handler;

    private async Task<EventId> PublishAsync(ushort kind, string content, IEnumerable<Tag>? extraTags = null)
    {
        var tags = new List<Tag>
        {
            Tag.PublicKey(_targetPubkey),
            Tag.Parse(["call-id", _callId]),
            Tag.Parse(["alt", AltText]),
        };
        if (extraTags is not null)
        {
            tags.AddRange(extraTags);
        }

        var innerEvent = new EventBuilder(new Kind(kind), content).Tags(tags).SignWithKeys(_bridgeKeys);

        var ephemeralKeys = Keys.Generate();
        var ciphertext = await NostrSigner.Keys(ephemeralKeys).Nip44Encrypt(_targetPubkey, innerEvent.AsJson());

        var outerEvent = new EventBuilder(new Kind(CallSignalKinds.WrapKind), ciphertext)
            .Tags([Tag.PublicKey(_targetPubkey)])
            .SignWithKeys(ephemeralKeys);

        var output = await _client!.SendEvent(outerEvent);
        if (output.success.Count == 0)
        {
            var reasons = string.Join("; ", output.failed.Select(f => $"{f.Key}: {f.Value}"));
            _logger.Error(
                "Failed to publish NIP-AC event (inner kind {InnerKind}, call-id {CallId}) to any relay: {Reasons}",
                kind,
                _callId,
                reasons);
            throw new InvalidOperationException($"No relay accepted the event (inner kind {kind}): {reasons}");
        }

        if (output.failed.Count > 0)
        {
            var reasons = string.Join("; ", output.failed.Select(f => $"{f.Key}: {f.Value}"));
            _logger.Warning(
                "NIP-AC event (inner kind {InnerKind}, call-id {CallId}) reached {SuccessCount} relay(s) but was rejected by others: {Reasons}",
                kind,
                _callId,
                output.success.Count,
                reasons);
        }

        return output.id;
    }

    internal async Task HandleWrappedEventAsync(Event wrapped)
    {
        if (_client is null)
        {
            return;
        }

        Event innerEvent;
        try
        {
            var plaintext = await NostrSigner.Keys(_bridgeKeys).Nip44Decrypt(wrapped.Author(), wrapped.Content());
            innerEvent = Event.FromJson(plaintext);
        }
        catch
        {
            // Not decryptable by us / malformed - ignore.
            return;
        }

        if (!innerEvent.VerifySignature() || !innerEvent.Author().Equals(_targetPubkey))
        {
            return;
        }

        var kind = innerEvent.Kind().AsU16();
        var content = innerEvent.Content();

        if (kind == CallSignalKinds.CallAnswer)
        {
            _pendingAnswer?.TrySetResult(content);
        }
        else if (kind == CallSignalKinds.IceCandidate)
        {
            var candidate = JsonSerializer.Deserialize<IceCandidatePayload>(content);
            if (candidate is not null)
            {
                _onIceCandidate?.Invoke(candidate);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.Shutdown();
            _client.Dispose();
        }
    }

    private sealed class WrapDispatcher(NostrSignalingClient owner) : HandleNotification
    {
        public Task HandleMsg(RelayUrl relayUrl, RelayMessage msg) => Task.CompletedTask;

        public Task Handle(RelayUrl relayUrl, string subscriptionId, Event evt) => owner.HandleWrappedEventAsync(evt);
    }
}
