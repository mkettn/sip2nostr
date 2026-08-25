namespace Sip2Nostr.Voicemail;

// Turns recorded voicemail PCM into text - the swappable speech-to-text
// engine behind TranscribedTextDeliveryBackend, selected by
// [voicemail.transcription].engine. See docs/voicemail.md.
public interface IVoicemailTranscriber : IAsyncDisposable
{
    // Returns null if nothing could be transcribed (silence, engine
    // failure) - the caller decides what to send in that case.
    Task<string?> TranscribeAsync(short[] samples, int sampleRate, CancellationToken ct);
}
