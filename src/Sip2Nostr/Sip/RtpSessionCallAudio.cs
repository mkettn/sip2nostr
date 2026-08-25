using SIPSorcery.Net;
using Sip2Nostr.Hub;

namespace Sip2Nostr.Sip;

// Adapts a SIP RTPSession to ICallAudio by relaying its RTP audio payloads
// unchanged - no decode/encode. sipsorcery's RTCPeerConnection is itself
// an RTPSession subclass, so this also works for the WebRTC leg
// (see NosCallSink).
public sealed class RtpSessionCallAudio : ICallAudio
{
    private readonly RTPSession session;

    public event Action<RtpAudioFrame>? OnAudioReceived;

    public RtpSessionCallAudio(RTPSession session)
    {
        this.session = session;
        session.OnRtpPacketReceived += HandleRtpPacketReceived;
    }

    public void Send(RtpAudioFrame frame)
    {
        session.SendRtpRaw(SDPMediaTypesEnum.audio, frame.Payload, frame.Timestamp, frame.MarkerBit, frame.PayloadType);
    }

    public void SendEncodedSample(uint durationRtpUnits, byte[] sample)
    {
        session.SendAudio(durationRtpUnits, sample);
    }

    private void HandleRtpPacketReceived(System.Net.IPEndPoint _, SDPMediaTypesEnum media, RTPPacket packet)
    {
        if (media != SDPMediaTypesEnum.audio)
        {
            return;
        }

        OnAudioReceived?.Invoke(new RtpAudioFrame(packet.Payload, packet.Header.Timestamp, packet.Header.MarkerBit, packet.Header.PayloadType));
    }
}
