namespace Sip2Nostr.Signaling;

// PLACEHOLDER kinds. There is no ratified NIP for call signaling yet
// (see README "Open questions"), and the exact event kind/tag layout
// NosCall expects must be read out of NosCall's source before this bridge
// can interop with it. These provisional values only let sip2nostr and a
// test client talk to themselves until that's confirmed.
public static class CallSignalKinds
{
    public const ushort CallOffer = 25050;
    public const ushort CallAnswer = 25051;
    public const ushort IceCandidate = 25052;
}
