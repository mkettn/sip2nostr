using Serilog;
using Sip2Nostr.Config;
using Sip2Nostr.Hub;
using Sip2Nostr.Shared;

namespace Sip2Nostr.Sinks;

// Local-only fallback for [nostr].enabled = false (dev/testing without a
// Nostr relay): plays a per-line test sound (or a sine wave if none is
// configured) on loop until the caller hangs up. Never records or
// forwards anywhere.
public sealed class LocalTestAudioSink(
    IReadOnlyList<LineConfig> lines,
    string configDirectory,
    ILogger logger) : ICallSink
{
    public async Task<bool> TryHandleAsync(Call call, CancellationToken ct)
    {
        var matchedLine = lines.FirstOrDefault(line => line.Label == call.LineLabel);
        var samples = ResolveTestAudio(matchedLine, call.AudioFormat.ClockRate);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var playTask = RtpAudioPlayback.PlayLoopAsync(call.Audio, samples, call.AudioFormat, cts.Token);
        await call.WhenRemoteHungUp;
        cts.Cancel();
        await playTask;

        return true;
    }

    private short[] ResolveTestAudio(LineConfig? matchedLine, int sampleRate)
    {
        if (!string.IsNullOrWhiteSpace(matchedLine?.Sound))
        {
            var soundPath = SoundFileResolver.Resolve(matchedLine.Sound, configDirectory, logger);
            if (soundPath is not null)
            {
                logger.Information("Playing local test sound {SoundPath} on loop for line {LineLabel}.", soundPath, matchedLine.Label);
                return SoundFileResolver.LoadPcmSamples(soundPath);
            }

            logger.Warning(
                "Configured sound file {SoundPath} for line {LineLabel} could not be used; sending sine wave instead.",
                matchedLine.Sound,
                matchedLine.Label);
        }
        else
        {
            logger.Information("No line sound configured; sending sine wave test audio.");
        }

        return RtpAudioPlayback.GenerateTone(440, 1.0, sampleRate);
    }
}
