using Nostr.Sdk;

namespace Sip2Nostr.Voicemail;

// Used when voicemail is enabled but the selected [voicemail].delivery
// mode's requirement isn't configured ("text" without
// [voicemail.transcription].model_path, "audio" without
// [voicemail.blossom].servers) - Program.cs logs a startup warning and
// falls back to this instead of AudioDeliveryBackend/
// TranscribedTextDeliveryBackend. Sends a plain-text notice rather than
// the recording itself; the recording is still saved to recordings_dir
// regardless (VoicemailSink does that unconditionally before a delivery
// backend ever sees the job), so this only affects what goes out as a
// DM - the caller's message is retrievable by hand from the bridge. See
// docs/voicemail.md.
public sealed class LocalOnlyDeliveryBackend : IVoicemailDeliveryBackend
{
    public bool RequiresPcm => false;

    public Task<(string Content, List<Tag> Tags, string Description, VoicemailContentKind Kind)> BuildContentAsync(VoicemailAudioJob job, CancellationToken ct)
    {
        var content =
            $"🎤 Voicemail from {job.CallerNumber} ({job.DurationSeconds}s) - the call wasn't answered. " +
            "No delivery method is configured for [voicemail].delivery; the recording is saved on the bridge.";
        var tags = new List<Tag> { Tag.Parse(["alt", "sip2nostr voicemail (no delivery configured)"]) };
        return Task.FromResult((content, tags, "voicemail notice (no delivery configured)", VoicemailContentKind.PrivateMessage));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
