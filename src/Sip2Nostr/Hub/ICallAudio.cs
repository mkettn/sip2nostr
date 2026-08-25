namespace Sip2Nostr.Hub;

// Bidirectional RTP audio for one call - see RtpAudioFrame for why RTP,
// not PCM, is the hub's fixed exchange format.
public interface ICallAudio
{
    event Action<RtpAudioFrame> OnAudioReceived;

    void Send(RtpAudioFrame frame);
}
