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
    private const string CallIdTagName = "call-id";
    private const string CallTypeVoice = "voice";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly Keys _bridgeKeys;
    private readonly PublicKey _targetPubkey;
    private readonly List<RelayUrl> _relays;
    private readonly string _callId;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<string> _calleeHangup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Client? _client;
    private TaskCompletionSource<string>? _pendingAnswer;
    private Action<IceCandidatePayload>? _onIceCandidate;

    // Completes with the reason string when target_npub ends the call from
    // its side - a NIP-AC hangup, or a reject if it never answered. The
    // callee hanging up is the only signal the SIP leg gets that the call
    // is over: nothing on the WebRTC leg is watched for it, so without
    // this the caller would sit on a dead call until they hang up
    // themselves.
    public Task<string> WhenCalleeHungUp => _calleeHangup.Task;

    public NostrSignalingClient(NostrConfig config, string callId, ILogger logger)
    {
        // BridgeNsec/TargetNpub are non-null here: only NosCallSink
        // constructs this, and only when [nostr].enabled - the same
        // condition ConfigLoader.ValidateBridgeIdentity/
        // ValidateTargetAndRelays already validated both under. They're
        // nullable on NostrConfig only because they're optional when
        // Nostr is disabled.
        _bridgeKeys = Keys.Parse(config.BridgeNsec!);
        _targetPubkey = PublicKey.Parse(config.TargetNpub!);
        _relays = config.Relays.Select(RelayUrl.Parse).ToList();
        _callId = callId;
        _logger = logger;
    }

    public async Task ConnectAsync()
    {
        _client = new ClientBuilder().Signer(NostrSigner.Keys(_bridgeKeys)).Build();
        var connectedRelays = await RelayConnector.ConnectAsync(_client, _relays, ConnectTimeout, _logger);
        if (connectedRelays.Count > 0)
        {
            _logger.Information(
                "Nostr signaling connected: {ConnectedCount}/{TotalCount} relay(s) reachable.",
                connectedRelays.Count,
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
        // See the constructor above - only ever called from Program.cs
        // inside its own [nostr].enabled check.
        var bridgeKeys = Keys.Parse(config.BridgeNsec!);
        var relays = config.Relays.Select(RelayUrl.Parse).ToList();
        var client = new ClientBuilder().Signer(NostrSigner.Keys(bridgeKeys)).Build();

        var connectedRelays = await RelayConnector.ConnectAsync(client, relays, ConnectTimeout, logger);
        if (connectedRelays.Count > 0)
        {
            logger.Information("Nostr connected: {ConnectedCount}/{TotalCount} relay(s) reachable.", connectedRelays.Count, relays.Count);
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

    // Content is the raw SDP offer string; call-type is required by
    // NIP-AC on offers only. sip2nostr only ever bridges audio.
    public Task<EventId> SendOfferAsync(string sdp) =>
        PublishAsync(CallSignalKinds.CallOffer, sdp, [Tag.Parse(["call-type", CallTypeVoice])]);

    public Task<EventId> SendIceCandidateAsync(IceCandidatePayload candidate) =>
        PublishAsync(CallSignalKinds.IceCandidate, JsonSerializer.Serialize(candidate));

    // Hangup, not Reject - see docs/voicemail.md.
    public Task<EventId> SendHangupAsync(string reason) =>
        PublishAsync(CallSignalKinds.Hangup, reason);

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
            Tag.Parse([CallIdTagName, _callId]),
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
        PublishOutcome.ThrowIfFailed(_logger, $"NIP-AC event (inner kind {kind}, call-id {_callId})", output);

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

        if (!IsForThisCall(innerEvent))
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
        else if (kind is CallSignalKinds.Hangup or CallSignalKinds.Reject)
        {
            // Both mean the same thing to a bridge that only ever
            // originates calls: target_npub isn't on this call any more.
            _calleeHangup.TrySetResult(content);
        }
    }

    // The relay subscription filters on our pubkey, not on this call, so a
    // second overlapping call's signaling would otherwise be dispatched
    // here too - and a hangup for the wrong call now tears down a live
    // one. A missing call-id tag is accepted rather than dropped: only a
    // mismatch is evidence the event belongs to another call.
    private bool IsForThisCall(Event innerEvent)
    {
        var callIdTag = innerEvent.Tags().ToVec().FirstOrDefault(tag => tag.KindStr() == CallIdTagName);
        return callIdTag is null || string.Equals(callIdTag.Content(), _callId, StringComparison.Ordinal);
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
