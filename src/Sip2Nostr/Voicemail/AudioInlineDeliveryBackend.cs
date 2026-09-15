using Nostr.Sdk;
using Sip2Nostr.Shared;

namespace Sip2Nostr.Voicemail;

// Default voicemail delivery backend: inlines the recording - already
// saved as Opus by Sinks/VoicemailSink.cs - as a base64 data: URI in the
// DM content. See docs/voicemail.md for the NIP-17 size budget this
// fits inside.
public sealed class AudioInlineDeliveryBackend : IVoicemailDeliveryBackend
{
    public bool RequiresPcm => false;

    public async Task<(string Content, List<Tag> Tags, string Description, VoicemailContentKind Kind)> BuildContentAsync(VoicemailAudioJob job, CancellationToken ct)
    {
        var audioBytes = await File.ReadAllBytesAsync(job.OpusPath, ct);

        // The real enforcement against encoded size - MaxRecordingSeconds
        // is only a heuristic ceiling on the configured value.
        if (audioBytes.Length > VoicemailBudget.MaxAudioBytes)
        {
            throw new InvalidOperationException(
                $"Recorded voicemail is {audioBytes.Length} bytes, over the {VoicemailBudget.MaxAudioBytes}-byte NIP-17 budget; sending it would fail.");
        }

        var dataUri = $"data:audio/ogg;base64,{Convert.ToBase64String(audioBytes)}";
        var content =
            $"🎤 Voicemail from {job.CallerNumber} ({job.DurationSeconds}s) - the call wasn't answered.\n\n{dataUri}";
        var tags = new List<Tag>
        {
            Tag.Parse(["alt", "sip2nostr voicemail"]),
            Tag.Parse(["duration", job.DurationSeconds.ToString()]),
        };
        return (content, tags, $"voicemail ({job.DurationSeconds}s, {audioBytes.Length} bytes, audio/ogg)", VoicemailContentKind.PrivateMessage);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
