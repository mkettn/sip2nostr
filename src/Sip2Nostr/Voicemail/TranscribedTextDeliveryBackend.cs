using System.Text;
using Nostr.Sdk;
using Serilog;
using Sip2Nostr.Shared;

namespace Sip2Nostr.Voicemail;

// [voicemail].delivery = "text": transcribes the recording via an
// IVoicemailTranscriber and sends the text instead of the audio itself.
// Transcribes job.Samples - the original recorded PCM - directly, rather
// than decoding job's saved Opus file back out, so transcription never
// runs on lossy-recompressed audio. A transcript is normally tiny, but
// it's still checked against the actual output (MaxTranscriptBytes)
// rather than trusted to stay small just because recordings are
// duration-capped - whisper.cpp's repetition-loop failure mode on
// silence/noise can produce far more text than any real voicemail would.
// See docs/voicemail.md.
//
// When transcription produces nothing, audioFallback (an
// AudioDeliveryBackend, wired up by Program.cs whenever
// [voicemail.blossom].servers is configured - regardless of the
// top-level delivery mode) delivers the recording as a Blossom upload
// instead of a plain-text notice, so a transcription failure degrades to
// "you get the audio" rather than "you get nothing." A failure in
// audioFallback itself falls through to the notice, same as having no
// fallback at all.
public sealed class TranscribedTextDeliveryBackend(
    IVoicemailTranscriber transcriber,
    IVoicemailDeliveryBackend? audioFallback,
    ILogger logger) : IVoicemailDeliveryBackend
{
    public bool RequiresPcm => true;

    public async Task<(string Content, List<Tag> Tags, string Description, VoicemailContentKind Kind)> BuildContentAsync(VoicemailAudioJob job, CancellationToken ct)
    {
        var text = await transcriber.TranscribeAsync(job.Samples, job.SampleRate, ct);

        if (string.IsNullOrWhiteSpace(text))
        {
            return await BuildFallbackContentAsync(job, ct);
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
        return (content, tags, $"voicemail transcript ({transcriptBytes} bytes)", VoicemailContentKind.PrivateMessage);
    }

    private async Task<(string Content, List<Tag> Tags, string Description, VoicemailContentKind Kind)> BuildFallbackContentAsync(VoicemailAudioJob job, CancellationToken ct)
    {
        if (audioFallback is not null)
        {
            try
            {
                return await audioFallback.BuildContentAsync(job, ct);
            }
            catch (Exception exception)
            {
                logger.Warning(
                    exception,
                    "Audio fallback failed for call {CallId} after transcription produced nothing; sending a notice instead.",
                    job.CallId);
            }
        }
        else
        {
            logger.Warning("Could not transcribe voicemail for call {CallId}; sending a notice instead.", job.CallId);
        }

        var fallbackContent =
            $"🎤 Voicemail from {job.CallerNumber} ({job.DurationSeconds}s) - the call wasn't answered. " +
            "Could not transcribe the recording; it's still saved on the bridge.";
        var fallbackTags = new List<Tag> { Tag.Parse(["alt", "sip2nostr voicemail (transcription failed)"]) };
        return (fallbackContent, fallbackTags, "voicemail notice (transcription failed)", VoicemailContentKind.PrivateMessage);
    }

    public async ValueTask DisposeAsync()
    {
        await transcriber.DisposeAsync();
        if (audioFallback is not null)
        {
            await audioFallback.DisposeAsync();
        }
    }
}
