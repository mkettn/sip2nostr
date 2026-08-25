namespace Sip2Nostr.Hub;

// Bidirectional RTP audio for one call - see RtpAudioFrame for why RTP,
// not PCM, is the hub's fixed exchange format for relayed audio.
public interface ICallAudio
{
    event Action<RtpAudioFrame> OnAudioReceived;

    // Relay an RTP frame exactly as received elsewhere (raw bridging).
    void Send(RtpAudioFrame frame);

    // Send one codec-encoded sample generated locally - matches
    // AudioExtrasSource.OnAudioSourceEncodedSample's signature, see
    // docs/hub-architecture.md for why.
    void SendEncodedSample(uint durationRtpUnits, byte[] sample);
}
