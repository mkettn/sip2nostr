namespace Sip2Nostr.Voicemail;

// Common base for anything VoicemailSender delivers as a Nostr DM to
// target_npub: a missed-call notice or a recorded voicemail (see
// VoicemailAudioJob). Both share one worker, one relay connection per
// batch, and one send path - only the DM content differs.
public abstract record SendJob(string CallId, string CallerNumber);

// A call that diverted into the voicemail flow (ring timeout, signaling
// failure) but never produced a recording worth sending - the caller
// hung up during the greeting/tone, or the recording was too short.
// CallBridge.RunVoicemailAsync enqueues exactly one of this or a
// VoicemailAudioJob per call, never both, so target_npub gets a single
// DM either way.
public sealed record MissedCallNoticeJob(string CallerNumber, string CallId) : SendJob(CallId, CallerNumber);
