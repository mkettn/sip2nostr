using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nostr.Sdk;
using Serilog;
using Sip2Nostr.Config;
// Nostr.Sdk declares its own HttpMethod (used by NIP-98 HTTP auth) that
// would otherwise collide with System.Net.Http's.
using HttpMethod = System.Net.Http.HttpMethod;

namespace Sip2Nostr.Voicemail;

// [voicemail].delivery = "audio": AES-256-GCM encrypts the recording,
// uploads the ciphertext to a Blossom (BUD-01/BUD-02) server - never
// the plaintext - and sends a NIP-17 kind 15 file message whose
// content is the file's URL and whose tags carry the decryption key. No
// recording-length cap beyond the shared max_recording_seconds sanity
// limit, since the DM itself only ever carries a URL, never the audio.
//
// If every configured server rejects the upload, falls back to
// FileDeliveryBackend's plain-text notice rather than dropping the job -
// this depends on a third-party HTTP server (DNS, TLS, rate limits all
// included) in a way the old inline-base64 approach never did, so a
// failed upload is a routine failure to plan for, not a rare edge case.
// See docs/voicemail.md.
public sealed class AudioDeliveryBackend(
    IReadOnlyList<Uri> servers,
    NostrConfig nostrConfig,
    ILogger logger) : IVoicemailDeliveryBackend
{
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Http = new() { Timeout = UploadTimeout };
    private readonly FileDeliveryBackend _fallback = new();

    public bool RequiresPcm => false;

    public async Task<(string Content, List<Tag> Tags, string Description, VoicemailContentKind Kind)> BuildContentAsync(VoicemailAudioJob job, CancellationToken ct)
    {
        var plaintext = await File.ReadAllBytesAsync(job.OpusPath, ct);

        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var authTag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using (var aesGcm = new AesGcm(key, authTag.Length))
        {
            aesGcm.Encrypt(nonce, plaintext, ciphertext, authTag);
        }

        // The uploaded blob is ciphertext-then-tag, one contiguous byte
        // array - a Blossom server hashes and later serves exactly the
        // bytes it's given, so that's also what "x" (the blob's own
        // identity) has to hash.
        var blob = new byte[ciphertext.Length + authTag.Length];
        ciphertext.CopyTo(blob, 0);
        authTag.CopyTo(blob, ciphertext.Length);
        var blobHashHex = Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant();

        // Non-null here: a job only ever reaches this queue via
        // VoicemailSink/NosCallSink, both only constructed when
        // [nostr].enabled (see Program.cs) - the same condition
        // ConfigLoader validated bridge_nsec under.
        var bridgeKeys = Keys.Parse(nostrConfig.BridgeNsec!);

        string? uploadedUrl = null;
        Exception? lastFailure = null;
        foreach (var server in servers)
        {
            try
            {
                uploadedUrl = await UploadAsync(server, blob, blobHashHex, bridgeKeys, ct);
                break;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                logger.Warning(
                    exception,
                    "Blossom server {Server} rejected the voicemail upload for call {CallId}; trying the next one.",
                    server,
                    job.CallId);
            }
        }

        if (uploadedUrl is null)
        {
            logger.Warning(
                lastFailure,
                "All {Count} configured [voicemail.blossom].servers rejected the upload for call {CallId}; sending a notice instead.",
                servers.Count,
                job.CallId);
            return await _fallback.BuildContentAsync(job, ct);
        }

        var tags = new List<Tag>
        {
            Tag.Parse(["alt", "sip2nostr voicemail"]),
            Tag.Parse(["file-type", "audio/ogg"]),
            Tag.Parse(["encryption-algorithm", "aes-gcm"]),
            Tag.Parse(["decryption-key", Convert.ToHexString(key).ToLowerInvariant()]),
            Tag.Parse(["decryption-nonce", Convert.ToHexString(nonce).ToLowerInvariant()]),
            Tag.Parse(["x", blobHashHex]),
            Tag.Parse(["ox", Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant()]),
            Tag.Parse(["size", blob.Length.ToString()]),
            Tag.Parse(["duration", job.DurationSeconds.ToString()]),
        };

        return (uploadedUrl, tags, $"voicemail via blossom ({job.DurationSeconds}s, {blob.Length} encrypted bytes)", VoicemailContentKind.FileMessage);
    }

    // BUD-02 upload: PUT the blob with an `Authorization: Nostr
    // <base64(signed kind 24242 event)>` header. No BUD-06 HEAD
    // preflight - servers accept a direct PUT per spec, and a voicemail
    // blob is small enough that skipping it costs nothing on a rejection.
    private async Task<string> UploadAsync(Uri server, byte[] blob, string blobHashHex, Keys bridgeKeys, CancellationToken ct)
    {
        var authEvent = BuildAuthEvent(blobHashHex, bridgeKeys);
        var authHeader = "Nostr " + Convert.ToBase64String(Encoding.UTF8.GetBytes(authEvent.AsJson()));

        // Not new Uri(server, "upload") - relative URI combination drops
        // the last path segment of a server URL with no trailing slash
        // (e.g. ".../api" + "upload" => ".../upload", silently losing
        // "/api"), which a configured [voicemail.blossom].servers entry
        // has no reason to have but shouldn't be able to break like this.
        var uploadUrl = new Uri($"{server.ToString().TrimEnd('/')}/upload");
        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
        {
            Content = new ByteArrayContent(blob),
        };
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);
        request.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{server} returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        using var descriptor = JsonDocument.Parse(body);
        if (!descriptor.RootElement.TryGetProperty("url", out var urlProperty) ||
            urlProperty.GetString() is not { Length: > 0 } url)
        {
            throw new InvalidOperationException($"{server} returned a successful response with no \"url\" field: {body}");
        }

        return url;
    }

    // BUD-02 auth event (kind 24242): "t" = the action it authorizes,
    // "x" = the sha256 of the exact blob it authorizes, "expiration" so a
    // captured auth header can't be replayed indefinitely.
    private static Event BuildAuthEvent(string blobHashHex, Keys bridgeKeys)
    {
        var expiration = Timestamp.Now().AddDuration(TimeSpan.FromMinutes(10));
        var tags = new List<Tag>
        {
            Tag.Parse(["t", "upload"]),
            Tag.Parse(["x", blobHashHex]),
            Tag.Parse(["expiration", expiration.AsSecs().ToString()]),
        };
        return new EventBuilder(new Kind(24242), "sip2nostr voicemail upload").Tags(tags).SignWithKeys(bridgeKeys);
    }

    public ValueTask DisposeAsync() => _fallback.DisposeAsync();
}
