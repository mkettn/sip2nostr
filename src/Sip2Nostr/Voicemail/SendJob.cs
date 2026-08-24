namespace Sip2Nostr.Voicemail;

// Common base for anything VoicemailSender delivers as a Nostr DM to
// target_npub: a missed-call notice or a recorded voicemail (see
// VoicemailAudioJob). Both share one worker, one relay connection per
// batch, and one send path - only the DM content differs.
public abstract record SendJob(string CallId, string CallerNumber);

// A call that diverted into the voicemail flow (ring timeout, signaling
// failure) without ever bridging over Nostr - enqueued immediately once
// that's known, before the greeting even plays, so the notice can arrive
// well ahead of (or even without) a following VoicemailAudioJob: the
// caller may hang up during the greeting, or the recording may end up
// too short to send, and this is still a missed call either way.
public sealed record MissedCallNoticeJob(string CallerNumber, string CallId) : SendJob(CallId, CallerNumber);
