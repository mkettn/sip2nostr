namespace Sip2Nostr.Hub;

// Bidirectional 8kHz mono 16-bit PCM audio for one call, regardless of the
// transport underneath (SIP RTP today; WebRTC or a modem's ALSA device are
// both PCM-shaped too, so the same interface covers them without change).
public interface ICallAudio
{
    event Action<short[]> OnAudioReceived;

    void Send(short[] samples);
}
