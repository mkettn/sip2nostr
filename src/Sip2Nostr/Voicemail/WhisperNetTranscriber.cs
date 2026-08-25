using System.Text;
using Serilog;
using SIPSorcery.Media;
using Whisper.net;

namespace Sip2Nostr.Voicemail;

// Offline speech-to-text via Whisper.net (a whisper.cpp binding) - no
// network access and no API key at transcription time, just a local
// GGML model file ([voicemail.transcription].model_path). whisper.cpp
// expects 16 kHz mono float samples in [-1, 1]; voicemail recordings are
// 8 kHz PCM (G.711's rate), so this resamples before handing samples
// over - SIPSorcery.Media.PcmResampler is already a project dependency,
// so no new one is needed just for that.
public sealed class WhisperNetTranscriber : IVoicemailTranscriber
{
    private const int WhisperSampleRate = 16000;

    private readonly WhisperFactory _factory;
    private readonly string? _language;
    private readonly ILogger _logger;

    public WhisperNetTranscriber(string modelPath, string? language, ILogger logger)
    {
        _factory = WhisperFactory.FromPath(modelPath);
        _language = language;
        _logger = logger;
    }

    public async Task<string?> TranscribeAsync(short[] samples, int sampleRate, CancellationToken ct)
    {
        var resampled = sampleRate == WhisperSampleRate
            ? samples
            : PcmResampler.Resample(samples, sampleRate, WhisperSampleRate);

        var floatSamples = new float[resampled.Length];
        for (var i = 0; i < resampled.Length; i++)
        {
            floatSamples[i] = resampled[i] / 32768f;
        }

        await using var processor = BuildProcessor();

        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(floatSamples, ct))
        {
            text.Append(segment.Text);
        }

        var result = text.ToString().Trim();
        if (result.Length == 0)
        {
            _logger.Warning("Whisper produced no transcript for a {SampleCount}-sample recording.", samples.Length);
            return null;
        }

        return result;
    }

    private WhisperProcessor BuildProcessor()
    {
        var builder = _factory.CreateBuilder();
        return (_language is null ? builder.WithLanguageDetection() : builder.WithLanguage(_language)).Build();
    }

    public ValueTask DisposeAsync()
    {
        _factory.Dispose();
        return ValueTask.CompletedTask;
    }
}
