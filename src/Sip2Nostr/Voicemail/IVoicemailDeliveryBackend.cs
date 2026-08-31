using Nostr.Sdk;

namespace Sip2Nostr.Voicemail;

// Turns a recorded voicemail into NIP-17 DM content - the swappable "how
// do we deliver a voicemail" step selected by [voicemail].delivery. See
// docs/voicemail.md.
public interface IVoicemailDeliveryBackend : IAsyncDisposable
{
    // Whether BuildContentAsync needs VoicemailAudioJob.Samples (the raw
    // recorded PCM) rather than just the saved Opus file at OpusPath.
    // VoicemailSink asks this - instead of re-deriving the same answer
    // from [voicemail].delivery itself - to decide whether to carry PCM
    // in the job at all.
    bool RequiresPcm { get; }

    Task<(string Content, List<Tag> Tags, string Description)> BuildContentAsync(VoicemailAudioJob job, CancellationToken ct);
}
