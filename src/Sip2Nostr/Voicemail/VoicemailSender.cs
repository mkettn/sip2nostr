using System.Threading.Channels;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using Nostr.Sdk;
using Serilog;
using Sip2Nostr.Config;
using Sip2Nostr.Shared;
using Sip2Nostr.Signaling;
using Sip2Nostr.Sip;

namespace Sip2Nostr.Voicemail;

// Background worker that owns delivery of missed-call notices and
// recorded voicemails - see docs/voicemail.md for the full flow.
public sealed class VoicemailSender : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private const int OpusResamplerQuality = 5;

    private readonly NostrConfig _nostrConfig;
    private readonly VoicemailConfig _voicemailConfig;
    private readonly ILogger _logger;
    private readonly Channel<SendJob> _queue = Channel.CreateUnbounded<SendJob>();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Task _worker;

    public VoicemailSender(NostrConfig nostrConfig, VoicemailConfig voicemailConfig, ILogger logger)
    {
        _nostrConfig = nostrConfig;
        _voicemailConfig = voicemailConfig;
        _logger = logger;
        _worker = Task.Run(() => RunAsync(_stopCts.Token));
    }

    public void Enqueue(SendJob job)
    {
        if (job is VoicemailAudioJob audio)
        {
            _logger.Information(
                "Queued voicemail for call {CallId} ({DurationSeconds}s) for delivery.",
                audio.CallId,
                audio.DurationSeconds);
        }
        else
        {
            _logger.Information("Queued missed-call notice for call {CallId} for delivery.", job.CallId);
        }

        _queue.Writer.TryWrite(job);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var batch = new List<SendJob>();
                while (_queue.Reader.TryRead(out var job))
                {
                    batch.Add(job);
                }

                if (batch.Count > 0)
                {
                    // Guards just this batch, so a failure here (e.g. a bad
                    // relay URL) can't permanently stop the worker - see
                    // docs/voicemail.md.
                    try
                    {
                        await SendBatchAsync(batch, ct).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        _logger.Error(
                            exception,
                            "Failed to send a batch of {Count} job(s); they will not be retried automatically.",
                            batch.Count);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var remaining = _queue.Reader.Count;
            if (remaining > 0)
            {
                _logger.Warning(
                    "Voicemail sender stopped with {Count} job(s) still queued, undelivered.",
                    remaining);
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Voicemail sender worker crashed; no further jobs will be sent this run.");
        }
    }

    private async Task SendBatchAsync(List<SendJob> batch, CancellationToken ct)
    {
        var bridgeKeys = Keys.Parse(_nostrConfig.BridgeNsec);
        var targetPubkey = PublicKey.Parse(_nostrConfig.TargetNpub);
        var relayUrls = (_voicemailConfig.DmRelays.Count > 0 ? _voicemailConfig.DmRelays : _nostrConfig.Relays)
            .Select(RelayUrl.Parse)
            .ToList();

        _logger.Information("Connecting to send {Count} queued job(s).", batch.Count);
        var client = new ClientBuilder().Signer(NostrSigner.Keys(bridgeKeys)).Build();
        try
        {
            var connectedRelays = await RelayConnector.ConnectAsync(client, relayUrls, ConnectTimeout, _logger);
            if (connectedRelays.Count == 0)
            {
                _logger.Error(
                    "None of the {TotalCount} configured voicemail relay(s) are reachable; {Count} job(s) undelivered.",
                    relayUrls.Count,
                    batch.Count);
                return;
            }

            foreach (var job in batch)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                await SendOneSafeAsync(client, targetPubkey, connectedRelays, job).ConfigureAwait(false);
            }
        }
        finally
        {
            await client.Shutdown();
            client.Dispose();
        }
    }

    private async Task SendOneSafeAsync(Client client, PublicKey targetPubkey, List<RelayUrl> connectedRelays, SendJob job)
    {
        try
        {
            var (content, tags, description) = job switch
            {
                MissedCallNoticeJob notice => BuildMissedCallNoticeContent(notice),
                VoicemailAudioJob audio => await BuildVoicemailContentAsync(audio),
                _ => throw new NotSupportedException($"Unknown send job type {job.GetType()}."),
            };

            var output = await client.SendPrivateMsgTo(connectedRelays, targetPubkey, content, tags);
            PublishOutcome.ThrowIfFailed(_logger, description, output);

            _logger.Information("Sent {Description} for call {CallId} to target_npub over Nostr.", description, job.CallId);
        }
        catch (Exception exception) when (job is VoicemailAudioJob audioJob)
        {
            _logger.Error(
                exception,
                "Failed to send voicemail for call {CallId} over Nostr; the recording is still saved at {WavPath}.",
                audioJob.CallId,
                audioJob.WavPath);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to send missed-call notice for call {CallId} over Nostr.", job.CallId);
        }
    }

    private static (string Content, List<Tag> Tags, string Description) BuildMissedCallNoticeContent(MissedCallNoticeJob notice)
    {
        var content = $"📞 Missed call from {notice.CallerNumber} - not answered.";
        var tags = new List<Tag> { Tag.Parse(["alt", "sip2nostr missed call"]) };
        return (content, tags, "missed-call notice");
    }

    private async Task<(string Content, List<Tag> Tags, string Description)> BuildVoicemailContentAsync(VoicemailAudioJob job)
    {
        var (audioBytes, mimeType) = await LoadAudioAsync(job);
        var dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(audioBytes)}";
        var content =
            $"🎤 Voicemail from {job.CallerNumber} ({job.DurationSeconds}s) - the call wasn't answered.\n\n{dataUri}";
        var tags = new List<Tag>
        {
            Tag.Parse(["alt", "sip2nostr voicemail"]),
            Tag.Parse(["duration", job.DurationSeconds.ToString()]),
        };
        return (content, tags, $"voicemail ({job.DurationSeconds}s, {audioBytes.Length} bytes, {mimeType})");
    }

    // No WAV fallback if Opus encoding fails - see docs/voicemail.md.
    private async Task<(byte[] AudioBytes, string MimeType)> LoadAudioAsync(VoicemailAudioJob job)
    {
        var wavBytes = await File.ReadAllBytesAsync(job.WavPath);
        var audioBytes = EncodeOpusOgg(wavBytes, job.SampleRate);

        // The real enforcement against encoded size - MaxRecordingSeconds
        // is only a heuristic ceiling on the configured value.
        if (audioBytes.Length > VoicemailBudget.MaxAudioBytes)
        {
            throw new InvalidOperationException(
                $"Encoded voicemail is {audioBytes.Length} bytes, over the {VoicemailBudget.MaxAudioBytes}-byte NIP-17 budget; sending it would fail.");
        }

        return (audioBytes, "audio/ogg");
    }

    private byte[] EncodeOpusOgg(byte[] wavBytes, int sampleRate)
    {
        var samples = WavEncoder.Decode(wavBytes);

        using var encoder = OpusCodecFactory.CreateEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = VoicemailBudget.OpusBitrateBps;

        // CBR, not VBR (the Concentus/libopus default) - see docs/voicemail.md.
        encoder.UseVBR = false;

        // DTX deliberately not enabled - see docs/voicemail.md.

        using var outputStream = new MemoryStream();

        // No using: OpusOggWriteStream isn't IDisposable - see docs/voicemail.md.
        var oggWriter = new OpusOggWriteStream(encoder, outputStream, new OpusTags(), sampleRate, OpusResamplerQuality, leaveOpen: true);
        oggWriter.WriteSamples(samples, 0, samples.Length);
        oggWriter.Finish();

        var oggBytes = outputStream.ToArray();
        _logger.Information("Encoded voicemail as Opus/OGG ({AudioBytes} bytes).", oggBytes.Length);
        return oggBytes;
    }

    public async ValueTask DisposeAsync()
    {
        _stopCts.Cancel();
        try
        {
            await _worker;
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            _stopCts.Dispose();
        }
    }
}
