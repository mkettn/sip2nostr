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
            Assert.Equal("audio", config.Voicemail.Delivery);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_InvalidDeliveryValue_Throws()
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\ndelivery = \"bogus\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(path));
            Assert.Contains("delivery", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_TextDeliveryWithoutModelPath_Throws()
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\ndelivery = \"text\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(path));
            Assert.Contains("model_path", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_TextDeliveryWithMissingModelFile_Throws()
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\ndelivery = \"text\"\n\n" +
            "[voicemail.transcription]\nmodel_path = \"does-not-exist.bin\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(path));
            Assert.Contains("model_path", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_TextDeliveryWithUnknownEngine_Throws()
    {
        var modelPath = WriteTempFile("fake-model-bytes");
        try
        {
            var toml = $"{MinimalValidToml}\n\n[voicemail]\ndelivery = \"text\"\n\n" +
                $"[voicemail.transcription]\nengine = \"bogus\"\nmodel_path = \"{EscapeTomlString(modelPath)}\"\n";
            var path = WriteTempConfig(toml);
            try
            {
                var exception = Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(path));
                Assert.Contains("engine", exception.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Fact]
    public void Load_TextDeliveryWithValidModelPath_Succeeds()
    {
        var modelPath = WriteTempFile("fake-model-bytes");
        try
        {
            var toml = $"{MinimalValidToml}\n\n[voicemail]\ndelivery = \"text\"\n\n" +
                $"[voicemail.transcription]\nmodel_path = \"{EscapeTomlString(modelPath)}\"\nlanguage = \"en\"\n";
            var path = WriteTempConfig(toml);
            try
            {
                var config = ConfigLoader.Load(path);
                Assert.Equal("text", config.Voicemail.Delivery);
                Assert.Equal("whisper", config.Voicemail.Transcription.Engine);
                Assert.Equal("en", config.Voicemail.Transcription.Language);
            }
            finally
            {
                File.Delete(path);
            }
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Fact]
    public void Load_TextDeliveryExceedingAudioBudget_Succeeds()
    {
        // The NIP-17/Opus size budget only constrains the "audio" backend -
        // a transcript stays tiny regardless of recording length.
        var modelPath = WriteTempFile("fake-model-bytes");
        try
        {
            var overBudget = VoicemailBudget.MaxRecordingSeconds + 100;
            var toml = $"{MinimalValidToml}\n\n[voicemail]\ndelivery = \"text\"\nmax_recording_seconds = {overBudget}\n\n" +
                $"[voicemail.transcription]\nmodel_path = \"{EscapeTomlString(modelPath)}\"\n";
            var path = WriteTempConfig(toml);
            try
            {
                var config = ConfigLoader.Load(path);
                Assert.Equal(overBudget, config.Voicemail.MaxRecordingSeconds);
            }
            finally
            {
                File.Delete(path);
            }
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [Fact]
    public void Load_TextDeliveryExceedingTextRecordingCeiling_Throws()
    {
        var modelPath = WriteTempFile("fake-model-bytes");
        try
        {
            var overCeiling = VoicemailBudget.MaxTextRecordingSeconds + 1;
            var toml = $"{MinimalValidToml}\n\n[voicemail]\ndelivery = \"text\"\nmax_recording_seconds = {overCeiling}\n\n" +
                $"[voicemail.transcription]\nmodel_path = \"{EscapeTomlString(modelPath)}\"\n";
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
        finally
        {
            File.Delete(modelPath);
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

    private static string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sip2nostr-test-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, content);
        return path;
    }

    private static string EscapeTomlString(string value) => value.Replace(@"\", @"\\");
}
