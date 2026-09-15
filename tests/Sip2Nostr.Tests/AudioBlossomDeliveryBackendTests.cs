using System.Net;
using System.Security.Cryptography;
using System.Text;
using Nostr.Sdk;
using Serilog;
using Sip2Nostr.Config;
using Sip2Nostr.Voicemail;
using Xunit;

namespace Sip2Nostr.Tests;

public class AudioBlossomDeliveryBackendTests
{
    // Arbitrary, freshly generated for this test file only - see
    // ConfigLoaderTests.BridgeNsec for why a real, parseable key is
    // needed rather than a placeholder.
    private const string BridgeNsec = "nsec1wlhduv429l0ggtp36zqv9jm767898l40hd62kyspgclhklzdthss37hrv3";

    [Fact]
    public async Task BuildContentAsync_UploadsEncryptedBlobAndReturnsFileMessage()
    {
        var plaintext = Encoding.UTF8.GetBytes("fake opus bytes - not real Opus, just a test payload");
        var opusPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(opusPath, plaintext);

        using var server = new FakeBlossomServer("https://cdn.example.com/uploaded-blob");
        try
        {
            var backend = new AudioBlossomDeliveryBackend(
                [server.BaseUri],
                new NostrConfig { BridgeNsec = BridgeNsec },
                Log.Logger);

            var job = new VoicemailAudioJob(opusPath, [], 8000, 12, "+15551234567", "call-1");
            var (content, tags, description, kind) = await backend.BuildContentAsync(job, CancellationToken.None);

            Assert.Equal(VoicemailContentKind.FileMessage, kind);
            Assert.Equal("https://cdn.example.com/uploaded-blob", content);
            Assert.Contains("blossom", description, StringComparison.OrdinalIgnoreCase);

            // The server received ciphertext (same length as the
            // plaintext, plus a 16-byte GCM auth tag), never the
            // plaintext bytes themselves.
            Assert.NotNull(server.ReceivedBody);
            Assert.Equal(plaintext.Length + 16, server.ReceivedBody!.Length);
            Assert.False(server.ReceivedBody[..plaintext.Length].AsSpan().SequenceEqual(plaintext));

            // The BUD-02 auth header carries a validly signed kind 24242
            // event whose "x" tag matches the sha256 of exactly the bytes
            // uploaded.
            Assert.NotNull(server.ReceivedAuthorizationHeader);
            Assert.StartsWith("Nostr ", server.ReceivedAuthorizationHeader);
            var authEventJson = Encoding.UTF8.GetString(Convert.FromBase64String(server.ReceivedAuthorizationHeader!["Nostr ".Length..]));
            var authEvent = Event.FromJson(authEventJson);
            Assert.True(authEvent.Verify());
            Assert.Equal(24242, authEvent.Kind().AsU16());
            var authTags = authEvent.Tags().ToVec();
            Assert.Contains(authTags, t => t.AsVec() is ["t", "upload"]);
            var expectedHash = Convert.ToHexString(SHA256.HashData(server.ReceivedBody!)).ToLowerInvariant();
            Assert.Contains(authTags, t => t.AsVec() is ["x", var hash] && hash == expectedHash);

            // Tags carry enough to decrypt the uploaded blob back to the
            // original recording.
            var flatTags = tags.Select(t => t.AsVec()).ToList();
            var keyHex = Assert.Single(flatTags, t => t[0] == "decryption-key")[1];
            var nonceHex = Assert.Single(flatTags, t => t[0] == "decryption-nonce")[1];
            var xHex = Assert.Single(flatTags, t => t[0] == "x")[1];
            var oxHex = Assert.Single(flatTags, t => t[0] == "ox")[1];
            Assert.Equal(expectedHash, xHex);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(plaintext)).ToLowerInvariant(), oxHex);

            var key = Convert.FromHexString(keyHex);
            var nonce = Convert.FromHexString(nonceHex);
            var blob = server.ReceivedBody!;
            var ciphertext = blob[..^16];
            var authTag = blob[^16..];
            var decrypted = new byte[ciphertext.Length];
            using (var aesGcm = new AesGcm(key, authTag.Length))
            {
                aesGcm.Decrypt(nonce, ciphertext, authTag, decrypted);
            }

            Assert.Equal(plaintext, decrypted);
        }
        finally
        {
            File.Delete(opusPath);
        }
    }

    [Fact]
    public async Task BuildContentAsync_FirstServerRejects_FallsBackToSecond()
    {
        var opusPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(opusPath, "irrelevant"u8.ToArray());

        using var failingServer = new FakeBlossomServer(null, HttpStatusCode.RequestEntityTooLarge);
        using var workingServer = new FakeBlossomServer("https://cdn.example.com/second-server-blob");
        try
        {
            var backend = new AudioBlossomDeliveryBackend(
                [failingServer.BaseUri, workingServer.BaseUri],
                new NostrConfig { BridgeNsec = BridgeNsec },
                Log.Logger);

            var job = new VoicemailAudioJob(opusPath, [], 8000, 3, "+15551234567", "call-2");
            var (content, _, _, _) = await backend.BuildContentAsync(job, CancellationToken.None);

            Assert.Equal("https://cdn.example.com/second-server-blob", content);
        }
        finally
        {
            File.Delete(opusPath);
        }
    }

    [Fact]
    public async Task BuildContentAsync_AllServersReject_Throws()
    {
        var opusPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(opusPath, "irrelevant"u8.ToArray());

        using var server = new FakeBlossomServer(null, HttpStatusCode.Forbidden);
        try
        {
            var backend = new AudioBlossomDeliveryBackend(
                [server.BaseUri],
                new NostrConfig { BridgeNsec = BridgeNsec },
                Log.Logger);

            var job = new VoicemailAudioJob(opusPath, [], 8000, 3, "+15551234567", "call-3");
            await Assert.ThrowsAsync<InvalidOperationException>(() => backend.BuildContentAsync(job, CancellationToken.None));
        }
        finally
        {
            File.Delete(opusPath);
        }
    }

    // Minimal BUD-02 server: accepts one PUT /upload, records what it
    // received, and replies with either a blob descriptor or a failure
    // status - just enough surface for AudioBlossomDeliveryBackend to
    // talk to.
    private sealed class FakeBlossomServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _acceptTask;

        public Uri BaseUri { get; }

        public byte[]? ReceivedBody { get; private set; }

        public string? ReceivedAuthorizationHeader { get; private set; }

        public FakeBlossomServer(string? uploadedUrl, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            var port = GetFreeTcpPort();
            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(BaseUri.ToString());
            _listener.Start();
            _acceptTask = AcceptOnceAsync(uploadedUrl, statusCode);
        }

        private async Task AcceptOnceAsync(string? uploadedUrl, HttpStatusCode statusCode)
        {
            var context = await _listener.GetContextAsync();
            ReceivedAuthorizationHeader = context.Request.Headers["Authorization"];
            using (var buffer = new MemoryStream())
            {
                await context.Request.InputStream.CopyToAsync(buffer);
                ReceivedBody = buffer.ToArray();
            }

            context.Response.StatusCode = (int)statusCode;
            var body = statusCode == HttpStatusCode.OK
                ? $$"""{"url": "{{uploadedUrl}}", "sha256": "0", "size": {{ReceivedBody.Length}}, "type": "application/octet-stream"}"""
                : """{"message": "rejected"}""";
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            await context.Response.OutputStream.WriteAsync(bodyBytes);
            context.Response.OutputStream.Close();
        }

        private static int GetFreeTcpPort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            try
            {
                _acceptTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best-effort - the test's own assertions already failed
                // by this point if the server never got a request.
            }

            _listener.Close();
        }
    }
}
