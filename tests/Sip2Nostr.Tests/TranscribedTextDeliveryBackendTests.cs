using Nostr.Sdk;
using Serilog;
using Sip2Nostr.Voicemail;
using Xunit;

namespace Sip2Nostr.Tests;

public class TranscribedTextDeliveryBackendTests
{
    private static VoicemailAudioJob MakeJob() => new("unused.opus", [], 8000, 10, "+15551234567", "call-1");

    [Fact]
    public async Task BuildContentAsync_TranscriptionSucceeds_ReturnsPrivateMessageWithTranscript()
    {
        var backend = new TranscribedTextDeliveryBackend(new FakeTranscriber("hello there"), audioFallback: null, Log.Logger);

        var (content, _, _, kind) = await backend.BuildContentAsync(MakeJob(), CancellationToken.None);

        Assert.Equal(VoicemailContentKind.PrivateMessage, kind);
        Assert.Contains("hello there", content);
    }

    [Fact]
    public async Task BuildContentAsync_TranscriptionFailsNoFallback_ReturnsNotice()
    {
        var backend = new TranscribedTextDeliveryBackend(new FakeTranscriber(null), audioFallback: null, Log.Logger);

        var (content, _, description, kind) = await backend.BuildContentAsync(MakeJob(), CancellationToken.None);

        Assert.Equal(VoicemailContentKind.PrivateMessage, kind);
        Assert.Contains("Could not transcribe", content);
        Assert.Contains("transcription failed", description);
    }

    [Fact]
    public async Task BuildContentAsync_TranscriptionFailsWithFallback_DelegatesToFallback()
    {
        var fallback = new FakeDeliveryBackend(_ =>
            ("https://cdn.example.com/blob", [Tag.Parse(["alt", "test"])], "voicemail via blossom", VoicemailContentKind.FileMessage));
        var backend = new TranscribedTextDeliveryBackend(new FakeTranscriber(null), fallback, Log.Logger);

        var (content, _, description, kind) = await backend.BuildContentAsync(MakeJob(), CancellationToken.None);

        // Kind has to come through as FileMessage here - VoicemailSender
        // decides how to send based on this value, so a fallback result
        // silently downgraded to PrivateMessage would send a file URL as
        // if it were message text instead of gift-wrapping a kind 15
        // rumor.
        Assert.Equal(VoicemailContentKind.FileMessage, kind);
        Assert.Equal("https://cdn.example.com/blob", content);
        Assert.Equal("voicemail via blossom", description);
    }

    [Fact]
    public async Task BuildContentAsync_TranscriptionFailsAndFallbackThrows_FallsThroughToNotice()
    {
        var fallback = new FakeDeliveryBackend(throwException: new InvalidOperationException("all servers rejected it"));
        var backend = new TranscribedTextDeliveryBackend(new FakeTranscriber(null), fallback, Log.Logger);

        var (content, _, description, kind) = await backend.BuildContentAsync(MakeJob(), CancellationToken.None);

        Assert.Equal(VoicemailContentKind.PrivateMessage, kind);
        Assert.Contains("Could not transcribe", content);
        Assert.Contains("transcription failed", description);
    }

    [Fact]
    public async Task DisposeAsync_DisposesBothTranscriberAndFallback()
    {
        var transcriber = new FakeTranscriber(null);
        var fallback = new FakeDeliveryBackend(_ => ("url", [], "desc", VoicemailContentKind.FileMessage));
        var backend = new TranscribedTextDeliveryBackend(transcriber, fallback, Log.Logger);

        await backend.DisposeAsync();

        Assert.True(transcriber.Disposed);
        Assert.True(fallback.Disposed);
    }

    private sealed class FakeTranscriber(string? result) : IVoicemailTranscriber
    {
        public bool Disposed { get; private set; }

        public Task<string?> TranscribeAsync(short[] samples, int sampleRate, CancellationToken ct) => Task.FromResult(result);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDeliveryBackend(
        Func<VoicemailAudioJob, (string Content, List<Tag> Tags, string Description, VoicemailContentKind Kind)>? result = null,
        Exception? throwException = null) : IVoicemailDeliveryBackend
    {
        public bool RequiresPcm => false;

        public bool Disposed { get; private set; }

        public Task<(string Content, List<Tag> Tags, string Description, VoicemailContentKind Kind)> BuildContentAsync(VoicemailAudioJob job, CancellationToken ct)
        {
            if (throwException is not null)
            {
                throw throwException;
            }

            return Task.FromResult(result!(job));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
