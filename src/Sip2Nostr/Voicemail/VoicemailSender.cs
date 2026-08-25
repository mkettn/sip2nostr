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
// recorded voicemails (see SendJob), decoupled from the call that
// produced them. CallBridge only ever enqueues a job and moves on - a
// call is never held up waiting on a relay connection, Opus encoding, or
// a slow publish, and the SIP dialog is torn down (BYE) right after the
// recording finishes rather than after the Nostr send.
//
// This also means no DM-relay connection is held open between sends:
// the worker wakes on a non-empty queue, connects once, drains everything
// queued at that point over that one connection (a backlog of several
// jobs costs one connect, not one per job), then disconnects and goes
// back to waiting. One instance is shared across every call for the life
// of the process - see BridgeService.
//
// Deliberately its own Client per batch, separate from the per-call
// NostrSignalingClient's pool: that keeps this worker's relay set (
// dm_relays, or [nostr].relays as a fallback) fully decoupled from
// whatever the in-progress call's signaling relays happen to be, and
// means TryConnect's reachability result is never polluted by relays
// something else already had connected.
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

    // Fire-and-forget by design: neither a missed-call notice nor a
    // recorded voicemail should ever block on delivery. TryWrite never
    // blocks or fails on an unbounded channel.
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
                    // Guards just this batch: SendBatchAsync's own setup
                    // (parsing keys/relays, connecting, shutting the Client
                    // down) isn't otherwise wrapped the way SendOneSafeAsync
                    // guards each individual send, and an unhandled
                    // exception here would otherwise escape to the catch
                    // below and permanently stop the worker for the rest of
                    // the process - silently dropping every job enqueued
                    // afterwards despite them still being logged as
                    // "queued".
                    try
                    {
                        await SendBatchAsync(batch, ct).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        // The batch is already drained from the channel by
                        // this point, so these specific jobs will not be
                        // retried automatically - only the worker itself
                        // survives, ready for the next Enqueue. Any
                        // voicemail recordings in the batch are still safe
                        // on disk regardless; a missed-call notice has
                        // nothing else backing it up.
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

            // Targets connectedRelays - the subset of dm_relays (or
            // [nostr].relays as a fallback) just verified reachable above,
            // not the full configured list - rather than Nostr.Sdk's own
            // separate NIP-17 relay-discovery logic via plain
            // SendPrivateMsg, which could resolve to a different relay set
            // entirely.
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

    // No WAV fallback: raw 8kHz 16-bit mono WAV runs 16,000 bytes/sec, so
    // MaxAudioBytes (30,400) only ever fits a 1.0-1.9s WAV, and
    // recordings under 1.0s are already dropped before this is called
    // (see CallBridge.RunVoicemailAsync) - a WAV fallback could never
    // actually succeed for a real voicemail, only fail later inside
    // SendPrivateMsgTo instead of here. If Opus encoding fails, the
    // caller's catch logs the WavPath and nothing is sent - the
    // recording is still safe on disk either way.
    private async Task<(byte[] AudioBytes, string MimeType)> LoadAudioAsync(VoicemailAudioJob job)
    {
        var wavBytes = await File.ReadAllBytesAsync(job.WavPath);
        var audioBytes = EncodeOpusOgg(wavBytes, job.SampleRate);

        // MaxRecordingSeconds is only a heuristic ceiling on the
        // *configured* recording length (see VoicemailBudget) - this is
        // the actual enforcement, against the real encoded size, so a
        // recording that slips past the heuristic (container overhead,
        // encoder overshoot, a long caller number) fails loudly here
        // instead of inside SendPrivateMsgTo as an opaque encryption or
        // relay error.
        if (audioBytes.Length > VoicemailBudget.MaxAudioBytes)
        {
            throw new InvalidOperationException(
                $"Encoded voicemail is {audioBytes.Length} bytes, over the {VoicemailBudget.MaxAudioBytes}-byte NIP-17 budget; sending it would fail.");
        }

        return (audioBytes, "audio/ogg");
    }

    // Re-encodes to Opus/OGG entirely in-process via Concentus (a pure
    // C# port of libopus) and Concentus.Oggfile (writes the Ogg
    // container around the encoded packets) - no external process, no
    // host dependency on ffmpeg being installed.
    private byte[] EncodeOpusOgg(byte[] wavBytes, int sampleRate)
    {
        var samples = WavEncoder.Decode(wavBytes);

        using var encoder = OpusCodecFactory.CreateEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = VoicemailBudget.OpusBitrateBps;

        // Without this, Concentus (like libopus) defaults to VBR,
        // where Bitrate is a target the encoder can exceed on
        // complex input - which would make it a false floor for the
        // size budget below. CBR bounds the encoded size close to
        // Bitrate regardless of content (verified empirically: see
        // VoicemailBudget's derivation of MaxRecordingSeconds).
        encoder.UseVBR = false;

        // DTX deliberately not enabled - see docs/voicemail.md blind
        // spots for why.

        using var outputStream = new MemoryStream();

        // OpusOggWriteStream deliberately isn't IDisposable - Finish()
        // (below) is what pads the trailing frame, writes the
        // end-of-stream page, and flushes; leaveOpen keeps
        // outputStream open afterwards so ToArray() below can still
        // read it.
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
