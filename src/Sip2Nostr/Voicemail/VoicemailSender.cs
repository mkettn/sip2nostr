using System.Threading.Channels;
using Nostr.Sdk;
using Serilog;
using Sip2Nostr.Config;
using Sip2Nostr.Signaling;

namespace Sip2Nostr.Voicemail;

// Background worker that owns delivery of missed-call notices and
// recorded voicemails - see docs/voicemail.md for the full flow. How a
// VoicemailAudioJob turns into DM content (inlined audio, a transcript,
// or an encrypted Blossom upload) is delegated to the configured
// IVoicemailDeliveryBackend; its result's Kind says whether that content
// goes out as a kind 14 private message or a kind 15 file message.
public sealed class VoicemailSender : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly NostrConfig _nostrConfig;
    private readonly VoicemailConfig _voicemailConfig;
    private readonly IVoicemailDeliveryBackend _deliveryBackend;
    private readonly ILogger _logger;
    private readonly Channel<SendJob> _queue = Channel.CreateUnbounded<SendJob>();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Task _worker;

    public VoicemailSender(
        NostrConfig nostrConfig,
        VoicemailConfig voicemailConfig,
        IVoicemailDeliveryBackend deliveryBackend,
        ILogger logger)
    {
        _nostrConfig = nostrConfig;
        _voicemailConfig = voicemailConfig;
        _deliveryBackend = deliveryBackend;
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

    private sealed record PreparedJob(SendJob Job, string Content, List<Tag> Tags, string Description, VoicemailContentKind Kind);

    private async Task SendBatchAsync(List<SendJob> batch, CancellationToken ct)
    {
        // Content is built for every job - including transcription, which
        // can take seconds to minutes - before any relay connection opens.
        // Holding a connection open (and idle) for that whole time risks
        // the relay dropping it, and blocks whatever else is queued behind
        // a slow job. See docs/voicemail.md.
        var preparedJobs = new List<PreparedJob>();
        foreach (var job in batch)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var prepared = await PrepareJobSafeAsync(job, ct).ConfigureAwait(false);
            if (prepared is not null)
            {
                preparedJobs.Add(prepared);
            }
        }

        if (preparedJobs.Count == 0)
        {
            return;
        }

        // Non-null here: a job only ever reaches this queue via
        // VoicemailSink/NosCallSink, both only constructed when
        // [nostr].enabled (see Program.cs) - the same condition
        // ConfigLoader validated bridge_nsec/target_npub under.
        var bridgeKeys = Keys.Parse(_nostrConfig.BridgeNsec!);
        var bridgePublicKey = bridgeKeys.PublicKey();
        var targetPubkey = PublicKey.Parse(_nostrConfig.TargetNpub!);
        var relayUrls = (_voicemailConfig.DmRelays.Count > 0 ? _voicemailConfig.DmRelays : _nostrConfig.Relays)
            .Select(RelayUrl.Parse)
            .ToList();

        _logger.Information("Connecting to send {Count} prepared job(s).", preparedJobs.Count);
        var client = new ClientBuilder().Signer(NostrSigner.Keys(bridgeKeys)).Build();
        try
        {
            var connectedRelays = await RelayConnector.ConnectAsync(client, relayUrls, ConnectTimeout, _logger);
            if (connectedRelays.Count == 0)
            {
                _logger.Error(
                    "None of the {TotalCount} configured voicemail relay(s) are reachable; {Count} job(s) undelivered.",
                    relayUrls.Count,
                    preparedJobs.Count);
                return;
            }

            foreach (var prepared in preparedJobs)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                await SendPreparedJobSafeAsync(client, bridgePublicKey, targetPubkey, connectedRelays, prepared).ConfigureAwait(false);
            }
        }
        finally
        {
            await client.Shutdown();
            client.Dispose();
        }
    }

    private async Task<PreparedJob?> PrepareJobSafeAsync(SendJob job, CancellationToken ct)
    {
        try
        {
            var (content, tags, description, kind) = job switch
            {
                MissedCallNoticeJob notice => BuildMissedCallNoticeContent(notice),
                VoicemailAudioJob audio => await _deliveryBackend.BuildContentAsync(audio, ct),
                _ => throw new NotSupportedException($"Unknown send job type {job.GetType()}."),
            };

            return new PreparedJob(job, content, tags, description, kind);
        }
        catch (Exception exception) when (job is VoicemailAudioJob audioJob)
        {
            _logger.Error(
                exception,
                "Failed to prepare voicemail for call {CallId} for sending; the recording is still saved at {OpusPath}.",
                audioJob.CallId,
                audioJob.OpusPath);
            return null;
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to prepare missed-call notice for call {CallId} for sending.", job.CallId);
            return null;
        }
    }

    private async Task SendPreparedJobSafeAsync(Client client, PublicKey bridgePublicKey, PublicKey targetPubkey, List<RelayUrl> connectedRelays, PreparedJob prepared)
    {
        try
        {
            // FileMessage (AudioBlossomDeliveryBackend) needs a kind 15
            // rumor built and gift-wrapped directly - Content is a file
            // URL, not message text, so SendPrivateMsgTo's kind 14 rumor
            // (used for every other backend) doesn't apply here.
            var output = prepared.Kind == VoicemailContentKind.FileMessage
                ? await client.GiftWrapTo(
                    connectedRelays,
                    targetPubkey,
                    new EventBuilder(new Kind(15), prepared.Content).Tags(prepared.Tags).Build(bridgePublicKey),
                    [])
                : await client.SendPrivateMsgTo(connectedRelays, targetPubkey, prepared.Content, prepared.Tags);
            PublishOutcome.ThrowIfFailed(_logger, prepared.Description, output);

            _logger.Information(
                "Sent {Description} for call {CallId} to target_npub over Nostr.",
                prepared.Description,
                prepared.Job.CallId);
        }
        catch (Exception exception) when (prepared.Job is VoicemailAudioJob audioJob)
        {
            _logger.Error(
                exception,
                "Failed to send voicemail for call {CallId} over Nostr; the recording is still saved at {OpusPath}.",
                audioJob.CallId,
                audioJob.OpusPath);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to send missed-call notice for call {CallId} over Nostr.", prepared.Job.CallId);
        }
    }

    private static (string Content, List<Tag> Tags, string Description, VoicemailContentKind Kind) BuildMissedCallNoticeContent(MissedCallNoticeJob notice)
    {
        var content = $"📞 Missed call from {notice.CallerNumber} - not answered.";
        var tags = new List<Tag> { Tag.Parse(["alt", "sip2nostr missed call"]) };
        return (content, tags, "missed-call notice", VoicemailContentKind.PrivateMessage);
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

        await _deliveryBackend.DisposeAsync();
    }
}
