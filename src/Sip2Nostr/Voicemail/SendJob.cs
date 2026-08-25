namespace Sip2Nostr.Voicemail;

// Common base for anything VoicemailSender delivers as a Nostr DM to
// target_npub - see docs/voicemail.md.
public abstract record SendJob(string CallId, string CallerNumber);

// A missed call that produced no recording worth sending. See
// docs/voicemail.md for the exactly-one-job-per-call guarantee.
public sealed record MissedCallNoticeJob(string CallerNumber, string CallId) : SendJob(CallId, CallerNumber);
