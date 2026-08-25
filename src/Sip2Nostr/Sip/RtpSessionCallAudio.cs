using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Sip2Nostr.Hub;

namespace Sip2Nostr.Sip;

// Adapts a SIP RTPSession to ICallAudio: decodes inbound RTP to PCM and
// encodes outbound PCM back to the negotiated codec. For G.711 the RTP
// clock rate equals the PCM sample rate, so a batch's duration in RTP
// units is just its sample count.
public sealed class RtpSessionCallAudio : ICallAudio
{
    private readonly RTPSession session;
    private readonly AudioFormat audioFormat;
    private readonly AudioEncoder encoder = new();

    public event Action<short[]>? OnAudioReceived;

    public RtpSessionCallAudio(RTPSession session, AudioFormat audioFormat)
    {
        this.session = session;
        this.audioFormat = audioFormat;
        session.OnRtpPacketReceived += HandleRtpPacketReceived;
    }

    public void Send(short[] samples)
    {
        var encoded = encoder.EncodeAudio(samples, audioFormat);
        session.SendAudio((uint)samples.Length, encoded);
    }

    private void HandleRtpPacketReceived(System.Net.IPEndPoint _, SDPMediaTypesEnum media, RTPPacket packet)
    {
        if (media != SDPMediaTypesEnum.audio)
        {
            return;
        }

        OnAudioReceived?.Invoke(encoder.DecodeAudio(packet.Payload, audioFormat));
    }
}
