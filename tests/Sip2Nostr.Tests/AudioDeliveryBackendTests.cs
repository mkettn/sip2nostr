using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Nostr.Sdk;
using Serilog;
using Sip2Nostr.Config;
using Sip2Nostr.Voicemail;
using Xunit;

namespace Sip2Nostr.Tests;

public class AudioDeliveryBackendTests
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
            var backend = new AudioDeliveryBackend(
                [BlossomServer.Parse(server.BaseUri.ToString())],
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
    public async Task BuildContentAsync_UnixSocketServer_UploadsOverSocket()
    {
        var plaintext = Encoding.UTF8.GetBytes("fake opus bytes over a unix socket");
        var opusPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(opusPath, plaintext);

        using var server = new FakeUnixBlossomServer("https://cdn.example.com/unix-socket-blob");
        try
        {
            await using var backend = new AudioDeliveryBackend(
                [BlossomServer.Parse($"unix:{server.SocketPath}")],
                new NostrConfig { BridgeNsec = BridgeNsec },
                Log.Logger);

            var job = new VoicemailAudioJob(opusPath, [], 8000, 5, "+15551234567", "call-unix");
            var (content, _, _, kind) = await backend.BuildContentAsync(job, CancellationToken.None);

            Assert.Equal(VoicemailContentKind.FileMessage, kind);
            Assert.Equal("https://cdn.example.com/unix-socket-blob", content);

            // Proves the request actually crossed the socket, not just
            // that BlossomServer.Parse split the string correctly.
            Assert.NotNull(server.ReceivedBody);
            Assert.Equal(plaintext.Length + 16, server.ReceivedBody!.Length);
            Assert.NotNull(server.ReceivedAuthorizationHeader);
            Assert.StartsWith("Nostr ", server.ReceivedAuthorizationHeader);
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
            var backend = new AudioDeliveryBackend(
                [BlossomServer.Parse(failingServer.BaseUri.ToString()), BlossomServer.Parse(workingServer.BaseUri.ToString())],
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
    public async Task BuildContentAsync_AllServersReject_FallsBackToFileNotice()
    {
        var opusPath = Path.GetTempFileName();
        await File.WriteAllBytesAsync(opusPath, "irrelevant"u8.ToArray());

        using var server = new FakeBlossomServer(null, HttpStatusCode.Forbidden);
        try
        {
            var backend = new AudioDeliveryBackend(
                [BlossomServer.Parse(server.BaseUri.ToString())],
                new NostrConfig { BridgeNsec = BridgeNsec },
                Log.Logger);

            var job = new VoicemailAudioJob(opusPath, [], 8000, 3, "+15551234567", "call-3");
            var (content, _, _, kind) = await backend.BuildContentAsync(job, CancellationToken.None);

            // A rejected upload degrades to the same plain-text notice
            // FileDeliveryBackend sends, rather than dropping the job -
            // see docs/voicemail.md.
            Assert.Equal(VoicemailContentKind.PrivateMessage, kind);
            Assert.Contains("+15551234567", content);
            Assert.Contains("saved on the bridge", content);
        }
        finally
        {
            File.Delete(opusPath);
        }
    }

    // Minimal BUD-02 server: accepts one PUT /upload, records what it
    // received, and replies with either a blob descriptor or a failure
    // status - just enough surface for AudioDeliveryBackend to
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

    // Exercises the Unix-socket transport end to end: a raw Socket bound
    // to a temp path, speaking just enough hand-rolled HTTP/1.1 to
    // receive AudioDeliveryBackend's PUT and reply with a blob
    // descriptor - HttpListener (used by FakeBlossomServer above) has no
    // Unix-socket support on .NET, hence rolling this by hand instead.
    private sealed class FakeUnixBlossomServer : IDisposable
    {
        private readonly Socket _listener;
        private readonly Task _acceptTask;

        public string SocketPath { get; }

        public byte[]? ReceivedBody { get; private set; }

        public string? ReceivedAuthorizationHeader { get; private set; }

        public FakeUnixBlossomServer(string uploadedUrl)
        {
            SocketPath = Path.Combine(Path.GetTempPath(), $"blossom-test-{Guid.NewGuid():N}.sock");
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen(1);
            _acceptTask = AcceptOnceAsync(uploadedUrl);
        }

        private async Task AcceptOnceAsync(string uploadedUrl)
        {
            using var client = await _listener.AcceptAsync();
            using var stream = new NetworkStream(client, ownsSocket: false);

            var received = new List<byte>();
            var readBuffer = new byte[4096];
            int headerEnd;
            while ((headerEnd = IndexOfHeaderEnd(received)) < 0)
            {
                var n = await stream.ReadAsync(readBuffer);
                if (n == 0)
                {
                    return;
                }

                received.AddRange(readBuffer[..n]);
            }

            var bytes = received.ToArray();
            var headers = ParseHeaders(Encoding.ASCII.GetString(bytes, 0, headerEnd));
            ReceivedAuthorizationHeader = headers.GetValueOrDefault("Authorization");
            var contentLength = int.Parse(headers["Content-Length"]);

            var body = new byte[contentLength];
            var alreadyRead = bytes[(headerEnd + 4)..];
            alreadyRead.CopyTo(body, 0);
            var bodyReceived = alreadyRead.Length;
            while (bodyReceived < contentLength)
            {
                var n = await stream.ReadAsync(body.AsMemory(bodyReceived));
                if (n == 0)
                {
                    break;
                }

                bodyReceived += n;
            }

            ReceivedBody = body;

            var responseBody = $$"""{"url": "{{uploadedUrl}}", "sha256": "0", "size": {{body.Length}}, "type": "application/octet-stream"}""";
            var responseBytes = Encoding.UTF8.GetBytes(responseBody);
            var response = "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {responseBytes.Length}\r\n" +
                "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            await stream.WriteAsync(responseBytes);
        }

        private static int IndexOfHeaderEnd(List<byte> buffer)
        {
            for (var i = 0; i + 3 < buffer.Count; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private static Dictionary<string, string> ParseHeaders(string headerText)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in headerText.Split("\r\n").Skip(1))
            {
                var separator = line.IndexOf(':');
                if (separator > 0)
                {
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
            }

            return headers;
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
            try
            {
                File.Delete(SocketPath);
            }
            catch
            {
                // Best-effort cleanup of the socket file.
            }
        }
    }
}
