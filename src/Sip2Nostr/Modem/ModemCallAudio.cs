using Serilog;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Sip2Nostr.Hub;

namespace Sip2Nostr.Modem;

// Adapts a raw duplex ALSA PCM device (AlsaPcmDevice) to ICallAudio.
// Unlike Sip.RtpSessionCallAudio (a zero-cost RTP relay over an existing
// RTPSession), a modem's audio never arrives RTP-shaped - ModemManager only
// controls call state over D-Bus, the PCM samples come straight off the
// device - so this is the one place in the modem call source that actually
// encodes/decodes G.711 (via SIPSorcery.Media.AudioEncoder, already a
// dependency), per docs/hub-architecture.md's "Why RTP, not PCM" section.
internal sealed class ModemCallAudio : ICallAudio, IAsyncDisposable
{
    private readonly AlsaPcmDevice _alsa;
    private readonly AudioFormat _audioFormat;
    private readonly AudioEncoder _encoder = new();
    private readonly ILogger _logger;
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _captureCts = new();
    private readonly Task _captureLoop;
    private uint _timestamp;

    public event Action<RtpAudioFrame>? OnAudioReceived;

    public ModemCallAudio(string alsaDeviceName, AudioFormat audioFormat, ILogger logger)
    {
        _audioFormat = audioFormat;
        _logger = logger;
        _alsa = new AlsaPcmDevice(alsaDeviceName, logger);
        // Reads are blocking P/Invoke calls into libasound; Task.Run gives
        // them a dedicated thread-pool thread instead of running inline on
        // whatever thread first subscribes to OnAudioReceived.
        _captureLoop = Task.Run(() => RunCaptureLoop(_captureCts.Token));
    }

    public void Send(RtpAudioFrame frame)
    {
        try
        {
            var pcm = _encoder.DecodeAudio(frame.Payload, _audioFormat);
            lock (_writeLock)
            {
                _alsa.Write(pcm);
            }
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "Failed to play a relayed RTP frame to the modem's ALSA device.");
        }
    }

    public void SendEncodedSample(uint durationRtpUnits, byte[] sample)
    {
        try
        {
            var pcm = _encoder.DecodeAudio(sample, _audioFormat);
            lock (_writeLock)
            {
                _alsa.Write(pcm);
            }
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "Failed to play a locally generated audio sample to the modem's ALSA device.");
        }
    }

    private void RunCaptureLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pcm = _alsa.Read();
                var encoded = _encoder.EncodeAudio(pcm, _audioFormat);
                OnAudioReceived?.Invoke(new RtpAudioFrame(encoded, _timestamp, 0, _audioFormat.FormatID));
                _timestamp += AlsaPcmDevice.FrameSamples;
            }
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            _logger.Error(exception, "Modem audio capture loop failed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _captureCts.Cancel();
        try
        {
            await _captureLoop;
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "Modem audio capture loop ended with an exception during shutdown.");
        }
        finally
        {
            _captureCts.Dispose();
            _alsa.Dispose();
        }
    }
}
