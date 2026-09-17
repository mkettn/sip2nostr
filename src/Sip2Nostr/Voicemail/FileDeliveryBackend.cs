using Nostr.Sdk;

namespace Sip2Nostr.Voicemail;

// [voicemail].delivery = "file" (the default): no upload, no
// transcription - the recording is just saved to recordings_dir (as
// every mode does, unconditionally, before a delivery backend ever runs
// - see Sinks/VoicemailSink.cs) and a plain-text notice goes out instead
// of the recording itself, for the operator to fetch and play by hand.
// The zero-setup option: nothing here needs [voicemail.blossom] or
// [voicemail.transcription] configured.
//
// Also what Program.cs falls back to, with a logged warning, when
// delivery = "audio"/"text" is selected but that mode's own requirement
// ([voicemail.blossom].servers / [voicemail.transcription].model_path)
// isn't configured - see docs/voicemail.md.
//
// "File" names this mode's behavior (the recording stays a local file,
// nothing leaves the bridge but a notice) - unrelated to
// VoicemailContentKind.FileMessage (NIP-17 kind 15), which this backend
// does not send; its notice is an ordinary kind 14 PrivateMessage.
public sealed class FileDeliveryBackend : IVoicemailDeliveryBackend
{
    public bool RequiresPcm => false;

    public Task<(string Content, List<Tag> Tags, string Description, VoicemailContentKind Kind)> BuildContentAsync(VoicemailAudioJob job, CancellationToken ct)
    {
        var content =
            $"🎤 Voicemail from {job.CallerNumber} ({job.DurationSeconds}s) - the call wasn't answered. " +
            "The recording is saved on the bridge.";
        var tags = new List<Tag> { Tag.Parse(["alt", "sip2nostr voicemail notice"]) };
        return Task.FromResult((content, tags, "voicemail notice (file delivery)", VoicemailContentKind.PrivateMessage));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
