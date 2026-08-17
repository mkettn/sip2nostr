using Sip2Nostr.Config;
using Sip2Nostr.Shared;
using Xunit;

namespace Sip2Nostr.Tests;

public class ConfigLoaderTests
{
    private const string MinimalValidToml = """
        [sip]
        provider_host = "sip.example.com"
        username = "user"
        password = "pass"

        [[lines]]
        uri = "sip:+15551234@sip.example.com"
        label = "main"

        [nostr]
        enabled = false
        relays = ["wss://relay.example.com"]
        bridge_nsec = "nsec1..."
        target_npub = "npub1..."
        """;

    [Fact]
    public void Load_DefaultVoicemailConfig_Succeeds()
    {
        var path = WriteTempConfig(MinimalValidToml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.False(config.Voicemail.Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("ring_timeout_seconds = 0", "ring_timeout_seconds")]
    [InlineData("ring_timeout_seconds = -5", "ring_timeout_seconds")]
    [InlineData("max_recording_seconds = 0", "max_recording_seconds")]
    [InlineData("max_recording_seconds = -1", "max_recording_seconds")]
    public void Load_NonPositiveVoicemailTimeout_Throws(string voicemailOverride, string expectedKeyInMessage)
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\n{voicemailOverride}\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(path));
            Assert.Contains(expectedKeyInMessage, exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MaxRecordingSecondsExceedsNip17Budget_Throws()
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nmax_recording_seconds = {VoicemailBudget.MaxRecordingSeconds + 1}\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(path));
            Assert.Contains("max_recording_seconds", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MaxRecordingSecondsAtNip17Budget_Succeeds()
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nmax_recording_seconds = {VoicemailBudget.MaxRecordingSeconds}\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal(VoicemailBudget.MaxRecordingSeconds, config.Voicemail.MaxRecordingSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempConfig(string toml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sip2nostr-test-{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, toml);
        return path;
    }
}
