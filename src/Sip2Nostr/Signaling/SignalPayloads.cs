namespace Sip2Nostr.Signaling;

// Offer/answer event content is the raw SDP string directly (no JSON
// envelope) per NIP-AC. Only the ICE candidate content is JSON.
public sealed record IceCandidatePayload(string Candidate, string? SdpMid, ushort SdpMLineIndex);
