namespace Sip2Nostr.Voicemail;

// A recorded voicemail, already saved to disk as a WAV, waiting to be
// encoded and sent. CallBridge enqueues one of these as soon as recording
// finishes and moves on immediately - the call is torn down without
// waiting on relay connectivity, ffmpeg, or a slow publish.
public sealed record VoicemailAudioJob(
    string WavPath,
    int SampleRate,
    int DurationSeconds,
    string CallerNumber,
    string CallId) : SendJob(CallId, CallerNumber);
