namespace Sip2Nostr.Voicemail;

// A recorded voicemail, already saved to disk as Ogg/Opus, waiting to be
// sent. VoicemailSink enqueues one of these as soon as recording (and
// encoding) finishes and moves on immediately - the call is torn down
// without waiting on relay connectivity or a slow publish.
public sealed record VoicemailAudioJob(
    string OggPath,
    int SampleRate,
    int DurationSeconds,
    string CallerNumber,
    string CallId) : SendJob(CallId, CallerNumber);
