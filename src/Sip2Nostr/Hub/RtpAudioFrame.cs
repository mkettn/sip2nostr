namespace Sip2Nostr.Hub;

// One RTP audio payload, exchanged unchanged between a source and a sink -
// the hub's fixed wire format. Carries just enough of RTPPacket's header
// to be resent as-is via RTPSession.SendRtpRaw. Both ends of a bridge
// must agree on the codec (see Call.AudioFormat) for PayloadType to mean
// the same thing on the receiving side; a source or sink that isn't
// RTP-shaped underneath (e.g. a modem capturing raw PCM) is responsible
// for encoding/decoding into this format itself - the hub never touches
// sample data.
public sealed record RtpAudioFrame(byte[] Payload, uint Timestamp, int MarkerBit, int PayloadType);
