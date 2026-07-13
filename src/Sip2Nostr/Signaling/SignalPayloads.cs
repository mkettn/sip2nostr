namespace Sip2Nostr.Signaling;

public sealed record CallOfferPayload(string Sdp);

public sealed record CallAnswerPayload(string Sdp);

public sealed record IceCandidatePayload(string Candidate, string? SdpMid, ushort SdpMLineIndex);
