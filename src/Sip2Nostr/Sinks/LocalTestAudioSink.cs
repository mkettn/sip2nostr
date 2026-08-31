using Serilog;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Sip2Nostr.Config;
using Sip2Nostr.Hub;
using Sip2Nostr.Shared;

namespace Sip2Nostr.Sinks;

// Local-only fallback for [nostr].enabled = false (dev/testing without a
// Nostr relay): answers straight away - there's no ring/answer decision
// to make here - then plays a per-line test sound (or a sine wave if none is
// configured) on loop until the caller hangs up, via SIPSorcery's own
// AudioExtrasSource wired into Call.Audio.SendEncodedSample. Never
// records or forwards anywhere.
public sealed class LocalTestAudioSink(
    IReadOnlyList<LineConfig> lines,
    string configDirectory,
    ILogger logger) : ICallSink
{
    public async Task<bool> TryHandleAsync(Call call, CancellationToken ct)
    {
        var matchedLine = lines.FirstOrDefault(line => line.Label == call.LineLabel);

        if (!await call.AnswerAsync())
        {
            logger.Information("Call {CallId} was gone before test audio could start.", call.CallId);
            return true;
        }

        var testAudioSource = new AudioExtrasSource(new AudioEncoder(), new AudioSourceOptions { AudioSource = AudioSourcesEnum.Silence });
        testAudioSource.SetAudioSourceFormat(call.AudioFormat);
        testAudioSource.OnAudioSourceEncodedSample += call.Audio.SendEncodedSample;
        ConfigureTestAudioSource(testAudioSource, matchedLine);

        try
        {
            await testAudioSource.StartAudio();
            await call.WhenRemoteHungUp;
        }
        finally
        {
            await testAudioSource.CloseAudio();
        }

        return true;
    }

    private void ConfigureTestAudioSource(AudioExtrasSource testAudioSource, LineConfig? matchedLine)
    {
        if (!string.IsNullOrWhiteSpace(matchedLine?.Sound))
        {
            var soundPath = SoundFileResolver.Resolve(matchedLine.Sound, configDirectory, logger);
            if (soundPath is not null)
            {
                logger.Information("Playing local test sound {SoundPath} on loop for line {LineLabel}.", soundPath, matchedLine.Label);
                testAudioSource.SetSource(new AudioSourceOptions
                {
                    AudioSource = AudioSourcesEnum.Music,
                    MusicFile = soundPath,
                    MusicInputSamplingRate = AudioSamplingRatesEnum.Rate8KHz,
                });
                return;
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

        testAudioSource.SetSource(AudioSourcesEnum.SineWave);
    }
}
