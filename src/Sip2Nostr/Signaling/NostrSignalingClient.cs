using System.Text.Json;
using Nostr.Sdk;
using Sip2Nostr.Config;

namespace Sip2Nostr.Signaling;

// Call signaling over Nostr (README component 2): gift-wraps an SDP offer
// to target_npub, waits for a gift-wrapped answer + ICE candidates back.
// One instance is used per active call.
public sealed class NostrSignalingClient : IAsyncDisposable
{
    private readonly Keys _bridgeKeys;
    private readonly PublicKey _targetPubkey;
    private readonly List<RelayUrl> _relays;
    private Client? _client;
    private TaskCompletionSource<string>? _pendingAnswer;
    private Action<IceCandidatePayload>? _onIceCandidate;

    public NostrSignalingClient(NostrConfig config)
    {
        _bridgeKeys = Keys.Parse(config.BridgeNsec);
        _targetPubkey = PublicKey.Parse(config.TargetNpub);
        _relays = config.Relays.Select(RelayUrl.Parse).ToList();
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

        _ = Task.Run(() => _client.HandleNotifications(new GiftWrapDispatcher(this)));

        var filter = new Filter()
            .Kind(Kind.FromStd(KindStandard.GiftWrap))
            .Pubkey(_bridgeKeys.PublicKey())
            .Since(Timestamp.Now());
        await _client.Subscribe(filter, null);
    }

    public async Task<EventId> SendOfferAsync(string sdp)
    {
        var payload = JsonSerializer.Serialize(new CallOfferPayload(sdp));
        var rumor = new EventBuilder(new Kind(CallSignalKinds.CallOffer), payload).Build(_bridgeKeys.PublicKey());
        var output = await _client!.GiftWrap(_targetPubkey, rumor, []);
        return output.id;
    }

    public async Task SendIceCandidateAsync(IceCandidatePayload candidate)
    {
        var payload = JsonSerializer.Serialize(candidate);
        var rumor = new EventBuilder(new Kind(CallSignalKinds.IceCandidate), payload).Build(_bridgeKeys.PublicKey());
        await _client!.GiftWrap(_targetPubkey, rumor, []);
    }

    public Task<string> WaitForAnswerAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAnswer = tcs;
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public void OnIceCandidateReceived(Action<IceCandidatePayload> handler) => _onIceCandidate = handler;

    internal async Task HandleGiftWrapAsync(Event giftWrap)
    {
        if (_client is null)
        {
            return;
        }

        UnwrappedGift unwrapped;
        try
        {
            unwrapped = await _client.UnwrapGiftWrap(giftWrap);
        }
        catch
        {
            // Not decryptable by us / not addressed to us - ignore.
            return;
        }

        if (!unwrapped.Sender().Equals(_targetPubkey))
        {
            return;
        }

        var rumor = unwrapped.Rumor();
        var kind = rumor.Kind().AsU16();
        var content = rumor.Content();

        if (kind == CallSignalKinds.CallAnswer)
        {
            var answer = JsonSerializer.Deserialize<CallAnswerPayload>(content);
            if (answer is not null)
            {
                _pendingAnswer?.TrySetResult(answer.Sdp);
            }
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

    private sealed class GiftWrapDispatcher(NostrSignalingClient owner) : HandleNotification
    {
        public Task HandleMsg(RelayUrl relayUrl, RelayMessage msg) => Task.CompletedTask;

        public Task Handle(RelayUrl relayUrl, string subscriptionId, Event evt) => owner.HandleGiftWrapAsync(evt);
    }
}
