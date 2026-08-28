using System.Text;
using Nostr.Sdk;
using Serilog;
using Sip2Nostr.Shared;

namespace Sip2Nostr.Voicemail;

// Alternative voicemail delivery backend: transcribes the recording via
// an IVoicemailTranscriber and sends the text instead of inlining audio.
// Transcribes job.Samples - the original recorded PCM - directly, rather
// than decoding job's saved Opus/OGG file back out, so transcription
// never runs on lossy-recompressed audio. A transcript is normally tiny
// compared to the NIP-17 budget that constrains AudioInlineDeliveryBackend,
// but it's still checked against the actual output (MaxTranscriptBytes)
// rather than trusted to stay small just because recordings are
// duration-capped - whisper.cpp's repetition-loop failure mode on
// silence/noise can produce far more text than any real voicemail would.
// See docs/voicemail.md.
public sealed class TranscribedTextDeliveryBackend(IVoicemailTranscriber transcriber, ILogger logger) : IVoicemailDeliveryBackend
{
    public async Task<(string Content, List<Tag> Tags, string Description)> BuildContentAsync(VoicemailAudioJob job, CancellationToken ct)
    {
        var text = await transcriber.TranscribeAsync(job.Samples, job.SampleRate, ct);

        if (string.IsNullOrWhiteSpace(text))
        {
            logger.Warning("Could not transcribe voicemail for call {CallId}; sending a notice instead.", job.CallId);
            var fallbackContent =
                $"🎤 Voicemail from {job.CallerNumber} ({job.DurationSeconds}s) - the call wasn't answered. " +
                "Could not transcribe the recording; it's still saved on the bridge.";
            var fallbackTags = new List<Tag> { Tag.Parse(["alt", "sip2nostr voicemail (transcription failed)"]) };
            return (fallbackContent, fallbackTags, "voicemail notice (transcription failed)");
        }

        var transcriptBytes = Encoding.UTF8.GetByteCount(text);
        if (transcriptBytes > VoicemailBudget.MaxTranscriptBytes)
        {
            throw new InvalidOperationException(
                $"Transcript is {transcriptBytes} bytes, over the {VoicemailBudget.MaxTranscriptBytes}-byte NIP-17 budget; sending it would fail.");
        }

        var content =
            $"🎤 Voicemail from {job.CallerNumber} ({job.DurationSeconds}s) - the call wasn't answered:\n\n{text}";
        var tags = new List<Tag>
        {
            Tag.Parse(["alt", "sip2nostr voicemail transcript"]),
            Tag.Parse(["duration", job.DurationSeconds.ToString()]),
        };
        return (content, tags, $"voicemail transcript ({transcriptBytes} bytes)");
    }

    public async ValueTask DisposeAsync() => await transcriber.DisposeAsync();
}
