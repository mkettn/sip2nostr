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

// Background worker that owns delivery of recorded voicemails, decoupled
// from the call that recorded them. CallBridge only ever enqueues a job
// and moves on - a call is never held up waiting on a relay connection,
// Opus encoding, or a slow publish, and the SIP dialog is torn down (BYE)
// right after the recording finishes rather than after the Nostr send.
//
// This also means no DM-relay connection is held open between voicemails:
// the worker wakes on a non-empty queue, connects once, drains everything
// queued at that point over that one connection (a backlog of several
// voicemails costs one connect, not one per voicemail), then disconnects
// and goes back to waiting. One instance is shared across every call for
// the life of the process - see BridgeService.
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
    private readonly Channel<VoicemailJob> _queue = Channel.CreateUnbounded<VoicemailJob>();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Task _worker;

    public VoicemailSender(NostrConfig nostrConfig, VoicemailConfig voicemailConfig, ILogger logger)
    {
        _nostrConfig = nostrConfig;
        _voicemailConfig = voicemailConfig;
        _logger = logger;
        _worker = Task.Run(() => RunAsync(_stopCts.Token));
    }

    // Fire-and-forget by design: recording a voicemail must never block on
    // delivery. TryWrite never blocks or fails on an unbounded channel.
    public void Enqueue(VoicemailJob job)
    {
        _logger.Information(
            "Queued voicemail for call {CallId} ({DurationSeconds}s) for delivery.",
            job.CallId,
            job.DurationSeconds);
        _queue.Writer.TryWrite(job);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var batch = new List<VoicemailJob>();
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
                    // the process - silently dropping every voicemail
                    // enqueued afterwards despite them still being logged
                    // as "queued".
                    try
                    {
                        await SendBatchAsync(batch, ct).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        // The batch is already drained from the channel by
                        // this point, so these specific voicemails will not
                        // be retried automatically - only the worker itself
                        // survives, ready for the next Enqueue.
                        _logger.Error(
                            exception,
                            "Failed to send a batch of {Count} voicemail(s); their recordings remain on disk, undelivered, and will not be retried automatically.",
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
                    "Voicemail sender stopped with {Count} voicemail(s) still queued; their recordings remain on disk, undelivered.",
                    remaining);
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Voicemail sender worker crashed; no further voicemails will be sent this run.");
        }
    }

    private async Task SendBatchAsync(List<VoicemailJob> batch, CancellationToken ct)
    {
        var bridgeKeys = Keys.Parse(_nostrConfig.BridgeNsec);
        var targetPubkey = PublicKey.Parse(_nostrConfig.TargetNpub);
        var relayUrls = (_voicemailConfig.DmRelays.Count > 0 ? _voicemailConfig.DmRelays : _nostrConfig.Relays)
            .Select(RelayUrl.Parse)
            .ToList();

        _logger.Information("Connecting to send {Count} queued voicemail(s).", batch.Count);
        var client = new ClientBuilder().Signer(NostrSigner.Keys(bridgeKeys)).Build();
        try
        {
            var connectedRelays = await RelayConnector.ConnectAsync(client, relayUrls, ConnectTimeout, _logger);
            if (connectedRelays.Count == 0)
            {
                _logger.Error(
                    "None of the {TotalCount} configured voicemail relay(s) are reachable; {Count} voicemail(s) remain on disk, undelivered.",
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

    private async Task SendOneSafeAsync(Client client, PublicKey targetPubkey, List<RelayUrl> connectedRelays, VoicemailJob job)
    {
        try
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

            // Targets connectedRelays - the subset of dm_relays (or
            // [nostr].relays as a fallback) just verified reachable above,
            // not the full configured list - rather than Nostr.Sdk's own
            // separate NIP-17 relay-discovery logic via plain
            // SendPrivateMsg, which could resolve to a different relay set
            // entirely.
            var output = await client.SendPrivateMsgTo(connectedRelays, targetPubkey, content, tags);
            PublishOutcome.ThrowIfFailed(_logger, "voicemail DM", output);

            _logger.Information(
                "Sent voicemail for call {CallId} ({DurationSeconds}s, {AudioBytes} bytes, {MimeType}) to target_npub over Nostr.",
                job.CallId,
                job.DurationSeconds,
                audioBytes.Length,
                mimeType);
        }
        catch (Exception exception)
        {
            _logger.Error(
                exception,
                "Failed to send voicemail for call {CallId} over Nostr; the recording is still saved at {WavPath}.",
                job.CallId,
                job.WavPath);
        }
    }

    // Re-encodes to Opus/OGG to keep the inlined base64 payload smaller,
    // falling back to sending the WAV directly if encoding fails for any
    // reason - see docs/voicemail.md. Pure managed code (Concentus is a
    // portable C# port of libopus, Concentus.Oggfile writes the Ogg
    // container around it) - no external process, no host dependency on
    // ffmpeg being installed.
    private async Task<(byte[] AudioBytes, string MimeType)> LoadAudioAsync(VoicemailJob job)
    {
        var wavBytes = await File.ReadAllBytesAsync(job.WavPath);
        var oggBytes = TryEncodeOpusOgg(wavBytes, job.SampleRate);
        var (audioBytes, mimeType) = oggBytes is not null ? (oggBytes, "audio/ogg") : (wavBytes, "audio/wav");

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

        return (audioBytes, mimeType);
    }

    private byte[]? TryEncodeOpusOgg(byte[] wavBytes, int sampleRate)
    {
        try
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
        catch (Exception exception)
        {
            _logger.Warning(exception, "Could not encode the voicemail as Opus/OGG; sending WAV instead.");
            return null;
        }
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
