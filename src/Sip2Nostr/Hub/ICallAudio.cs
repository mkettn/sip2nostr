namespace Sip2Nostr.Hub;

// Bidirectional RTP audio for one call - see RtpAudioFrame for why RTP,
// not PCM, is the hub's fixed exchange format for relayed audio.
public interface ICallAudio
{
    event Action<RtpAudioFrame> OnAudioReceived;

    // Relay an RTP frame exactly as received elsewhere - used for raw
    // bridging (NosCallSink), where the frame's own timestamp/marker/
    // payload-type must be preserved unchanged.
    void Send(RtpAudioFrame frame);

    // Send one codec-encoded sample generated locally, letting the
    // underlying RTP session manage timestamp/sequence/marker bit itself.
    // Matches SIPSorcery's own AudioExtrasSource.OnAudioSourceEncodedSample
    // signature so a sink that generates audio (a greeting, a tone) can
    // wire that off-the-shelf player straight into it instead of
    // reimplementing RTP framing/pacing.
    void SendEncodedSample(uint durationRtpUnits, byte[] sample);
}
