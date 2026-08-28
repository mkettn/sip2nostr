namespace Sip2Nostr.Voicemail;

// A recorded voicemail, already saved to disk as Ogg/Opus, waiting to be
// sent. VoicemailSink enqueues one of these as soon as recording (and
// encoding) finishes and moves on immediately - the call is torn down
// without waiting on relay connectivity or a slow publish. Samples
// carries the original recorded PCM alongside OggPath so
// TranscribedTextDeliveryBackend (for whisper.cpp) reads the recording
// directly rather than decoding it back out of the lossy Opus/OGG file;
// AudioInlineDeliveryBackend sends OggPath's file as-is and never reads
// Samples, so VoicemailSink only populates it when
// [voicemail].delivery = "text" - otherwise it's empty.
public sealed record VoicemailAudioJob(
    string OggPath,
    short[] Samples,
    int SampleRate,
    int DurationSeconds,
    string CallerNumber,
    string CallId) : SendJob(CallId, CallerNumber);
