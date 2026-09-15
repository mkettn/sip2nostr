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

    // Kind says whether Content/Tags describe a kind 14 private-message
    // rumor (VoicemailSender sends it via Client.SendPrivateMsgTo, as
    // TranscribedTextDeliveryBackend/LocalOnlyDeliveryBackend do) or a
    // kind 15 file-message rumor it has to build and gift-wrap itself
    // instead (Content is a file URL, not message text - see
    // Voicemail/AudioDeliveryBackend.cs). It's part of the result rather
    // than a fixed per-backend property because
    // TranscribedTextDeliveryBackend can return either, depending on
    // whether it ends up delegating to an audio fallback for one
    // particular call - see docs/voicemail.md.
    Task<(string Content, List<Tag> Tags, string Description, VoicemailContentKind Kind)> BuildContentAsync(VoicemailAudioJob job, CancellationToken ct);
}

public enum VoicemailContentKind
{
    PrivateMessage,
    FileMessage,
}
