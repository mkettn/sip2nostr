using Sip2Nostr.Voicemail;
using Xunit;

namespace Sip2Nostr.Tests;

public class LocalOnlyDeliveryBackendTests
{
    [Fact]
    public async Task BuildContentAsync_ReturnsPrivateMessageNoticeWithoutTouchingTheRecording()
    {
        var backend = new LocalOnlyDeliveryBackend();
        var job = new VoicemailAudioJob("does-not-exist.opus", [], 8000, 42, "+15551234567", "call-1");

        var (content, tags, description, kind) = await backend.BuildContentAsync(job, CancellationToken.None);

        Assert.Equal(VoicemailContentKind.PrivateMessage, kind);
        Assert.Contains("+15551234567", content);
        Assert.Contains("saved on the bridge", content);
        Assert.Contains("no delivery configured", description);
        Assert.NotEmpty(tags);
    }

    [Fact]
    public void RequiresPcm_IsFalse()
    {
        // Never reads job.Samples - the notice doesn't depend on the
        // recording, so VoicemailSink shouldn't bother populating it.
        Assert.False(new LocalOnlyDeliveryBackend().RequiresPcm);
    }
}
