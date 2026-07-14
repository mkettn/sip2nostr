namespace Sip2Nostr.Signaling;

// NIP-AC ("WebRTC Calls") - draft Nostr NIP for peer-to-peer call
// signaling, confirmed against NosCall's real implementation
// (lib/core/call/nip_ac_protocol.dart, lib/call/calling_controller.dart).
// See docs/propagating-to-nostr.md.
public static class CallSignalKinds
{
    public const ushort CallOffer = 25050;
    public const ushort CallAnswer = 25051;
    public const ushort IceCandidate = 25052;
    public const ushort Hangup = 25053;
    public const ushort Reject = 25054;

    // Ephemeral wrap kind: NIP-AC's variant of a NIP-59 gift wrap - a
    // single NIP-44 encryption layer with no seal, signed by a fresh
    // ephemeral keypair per message, published as this kind instead of
    // the standard gift-wrap kind (1059).
    public const ushort WrapKind = 21059;
}
