using Nostr.Sdk;

namespace Sip2Nostr.Voicemail;

// Turns a recorded voicemail into NIP-17 DM content - the swappable "how
// do we deliver a voicemail" step selected by [voicemail].delivery. See
// docs/voicemail.md.
public interface IVoicemailDeliveryBackend : IAsyncDisposable
{
    Task<(string Content, List<Tag> Tags, string Description)> BuildContentAsync(VoicemailAudioJob job, CancellationToken ct);
}
