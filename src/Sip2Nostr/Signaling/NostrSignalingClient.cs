using System.Text.Json;
using Nostr.Sdk;
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

    private readonly Keys _bridgeKeys;
    private readonly PublicKey _targetPubkey;
    private readonly List<RelayUrl> _relays;
    private readonly string _callId;
    private Client? _client;
    private TaskCompletionSource<string>? _pendingAnswer;
    private Action<IceCandidatePayload>? _onIceCandidate;

    public NostrSignalingClient(NostrConfig config, string callId)
    {
        _bridgeKeys = Keys.Parse(config.BridgeNsec);
        _targetPubkey = PublicKey.Parse(config.TargetNpub);
        _relays = config.Relays.Select(RelayUrl.Parse).ToList();
        _callId = callId;
    }

    public async Task ConnectAsync()
    {
        var signer = NostrSigner.Keys(_bridgeKeys);
        _client = new ClientBuilder().Signer(signer).Build();
        foreach (var relay in _relays)
        {
            await _client.AddRelay(relay);
        }

        await _client.Connect();

        _ = Task.Run(() => _client.HandleNotifications(new WrapDispatcher(this)));

        var filter = new Filter()
            .Kind(new Kind(CallSignalKinds.WrapKind))
            .Pubkey(_bridgeKeys.PublicKey())
            .Since(Timestamp.Now());
        await _client.Subscribe(filter, null);
    }

    // Content is the raw SDP offer string; call-type is required by
    // NIP-AC on offers only. sip2nostr only ever bridges audio.
    public Task<EventId> SendOfferAsync(string sdp) =>
        PublishAsync(CallSignalKinds.CallOffer, sdp, [Tag.Parse(["call-type", CallTypeVoice])]);

    public Task<EventId> SendIceCandidateAsync(IceCandidatePayload candidate) =>
        PublishAsync(CallSignalKinds.IceCandidate, JsonSerializer.Serialize(candidate));

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

        var outerTags = new List<Tag> { Tag.PublicKey(_targetPubkey) };
        if (kind == CallSignalKinds.CallOffer)
        {
            outerTags.Add(Tag.Parse(["k", CallSignalKinds.CallOffer.ToString()]));
        }

        var outerEvent = new EventBuilder(new Kind(CallSignalKinds.WrapKind), ciphertext)
            .Tags(outerTags)
            .SignWithKeys(ephemeralKeys);

        var output = await _client!.SendEvent(outerEvent);
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
