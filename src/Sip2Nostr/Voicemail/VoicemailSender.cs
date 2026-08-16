using System.Threading.Channels;
using Nostr.Sdk;
using Serilog;
using Sip2Nostr.Config;
using Sip2Nostr.Signaling;

namespace Sip2Nostr.Voicemail;

// Background worker that owns delivery of recorded voicemails, decoupled
// from the call that recorded them. CallBridge only ever enqueues a job
// and moves on - a call is never held up waiting on a relay connection,
// ffmpeg, or a slow publish, and the SIP dialog is torn down (BYE) right
// after the recording finishes rather than after the Nostr send.
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
                    await SendBatchAsync(batch, ct).ConfigureAwait(false);
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

                await SendOneSafeAsync(client, targetPubkey, relayUrls, job).ConfigureAwait(false);
            }
        }
        finally
        {
            await client.Shutdown();
            client.Dispose();
        }
    }

    private async Task SendOneSafeAsync(Client client, PublicKey targetPubkey, List<RelayUrl> relayUrls, VoicemailJob job)
    {
        try
        {
            var (audioBytes, mimeType) = await LoadAudioAsync(job.WavPath);
            var dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(audioBytes)}";
            var content =
                $"🎤 Voicemail from {job.CallerNumber} ({job.DurationSeconds}s) - the call wasn't answered.\n\n{dataUri}";
            var tags = new List<Tag>
            {
                Tag.Parse(["alt", "sip2nostr voicemail"]),
                Tag.Parse(["duration", job.DurationSeconds.ToString()]),
            };

            // Explicitly targets the exact relays just connected above
            // (dm_relays, or [nostr].relays as a fallback), rather than
            // Nostr.Sdk's own separate NIP-17 relay-discovery logic via
            // plain SendPrivateMsg - which could resolve to a different
            // relay set than the one this worker just verified reachable.
            var output = await client.SendPrivateMsgTo(relayUrls, targetPubkey, content, tags);
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
    // falling back to sending the WAV directly if ffmpeg is missing or
    // fails - see docs/voicemail.md. The .ogg is a transport artifact, not
    // part of the archive, so it's deleted once read; the WAV stays.
    private async Task<(byte[] AudioBytes, string MimeType)> LoadAudioAsync(string wavPath)
    {
        var oggPath = TryConvertToOpusOgg(wavPath, Path.ChangeExtension(wavPath, ".ogg"));
        if (oggPath is not null)
        {
            var audioBytes = await File.ReadAllBytesAsync(oggPath);
            TryDeleteTransientFile(oggPath);
            return (audioBytes, "audio/ogg");
        }

        return (await File.ReadAllBytesAsync(wavPath), "audio/wav");
    }

    private string? TryConvertToOpusOgg(string wavPath, string oggPath)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList =
                {
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-y",
                    "-i",
                    wavPath,
                    "-c:a",
                    "libopus",
                    "-b:a",
                    "16k",
                    oggPath,
                },
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                _logger.Warning("Could not start ffmpeg to encode the voicemail as Opus/OGG; sending WAV instead.");
                return null;
            }

            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                _logger.Warning("ffmpeg failed to encode the voicemail as Opus/OGG; sending WAV instead: {FfmpegError}", error.Trim());
                return null;
            }

            _logger.Information("Encoded voicemail as Opus/OGG at {OggPath}.", oggPath);
            return oggPath;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // IOException covers ffmpeg dying mid-run (e.g. killed) while
            // its stderr pipe is still being read - not just ffmpeg being
            // missing. Every failure here has the same "just send the WAV"
            // recovery, so this is deliberately broad.
            _logger.Warning(exception, "Could not run ffmpeg to encode the voicemail as Opus/OGG; sending WAV instead. Install ffmpeg to send smaller recordings.");
            return null;
        }
    }

    private void TryDeleteTransientFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "Could not delete transient file {Path}.", path);
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
