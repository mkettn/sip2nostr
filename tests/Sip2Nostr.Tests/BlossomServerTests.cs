using Sip2Nostr.Voicemail;
using Xunit;

namespace Sip2Nostr.Tests;

public class BlossomServerTests
{
    [Fact]
    public void Parse_HttpUrl_KeepsItAsRequestUriWithNoSocketPath()
    {
        var server = BlossomServer.Parse("https://blossom.example.com");

        Assert.Equal(new Uri("https://blossom.example.com"), server.RequestUri);
        Assert.Null(server.SocketPath);
        Assert.Equal("https://blossom.example.com/", server.ToString());
    }

    [Fact]
    public void Parse_UnixEntry_SplitsOffThePathAndKeepsAPlaceholderRequestUri()
    {
        var server = BlossomServer.Parse("unix:/run/blossom/blossom.sock");

        Assert.Equal("/run/blossom/blossom.sock", server.SocketPath);
        Assert.True(server.RequestUri.IsAbsoluteUri);
        Assert.Equal("unix:/run/blossom/blossom.sock", server.ToString());
    }
}
