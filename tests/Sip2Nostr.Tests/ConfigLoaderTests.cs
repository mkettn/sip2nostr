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
            Assert.Equal("{timestamp}-{caller}.opus", config.Voicemail.RecordingFilename);
            Assert.Equal(VoicemailBudget.MaxTextRecordingSeconds, config.Voicemail.MaxTextRecordingSeconds);
            Assert.Equal(VoicemailBudget.OpusResamplerQuality, config.Voicemail.OpusResamplerQuality);
            Assert.Equal(15, config.WebRtc.ConnectionLossGraceSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Load_InvalidConnectionLossGraceSeconds_Throws(int graceSeconds)
    {
        var toml = $"{MinimalValidToml}\n\n[webrtc]\nconnection_loss_grace_seconds = {graceSeconds}\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(path));
            Assert.Contains("connection_loss_grace_seconds", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CustomConnectionLossGraceSeconds_Succeeds()
    {
        var toml = $"{MinimalValidToml}\n\n[webrtc]\nconnection_loss_grace_seconds = 30\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal(30, config.WebRtc.ConnectionLossGraceSeconds);
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
    [InlineData("max_text_recording_seconds = 0", "max_text_recording_seconds")]
    [InlineData("max_text_recording_seconds = -5", "max_text_recording_seconds")]
    [InlineData("max_text_recording_seconds = 3601", "max_text_recording_seconds")]
    public void Load_InvalidVoicemailTimeout_Throws(string voicemailOverride, string expectedKeyInMessage)
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
    public void Load_MaxTextRecordingSecondsAtCeiling_Succeeds()
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nmax_text_recording_seconds = {VoicemailBudget.MaxTextRecordingSecondsCeiling}\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal(VoicemailBudget.MaxTextRecordingSecondsCeiling, config.Voicemail.MaxTextRecordingSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("/tmp/{call_id}.opus")]
    [InlineData("../{call_id}.opus")]
    [InlineData("{caller}/../../etc/{call_id}.opus")]
    public void Load_RecordingFilenameEscapingRecordingsDir_Throws(string recordingFilename)
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nrecording_filename = \"{EscapeTomlString(recordingFilename)}\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(path));
            Assert.Contains("recording_filename", exception.Message);
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

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Load_OpusResamplerQualityOutOfRange_Throws(int quality)
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nopus_resampler_quality = {quality}\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(path));
            Assert.Contains("opus_resampler_quality", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void Load_OpusResamplerQualityAtBoundary_Succeeds(int quality)
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nopus_resampler_quality = {quality}\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal(quality, config.Voicemail.OpusResamplerQuality);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MaxRecordingSecondsBelowConfiguredTextCeiling_Throws()
    {
        // max_text_recording_seconds lowers the "text" ceiling below the
        // default (VoicemailBudget.MaxTextRecordingSeconds) - ConfigLoader
        // must validate against the configured value, not the constant.
        var modelPath = WriteTempFile("fake-model-bytes");
        try
        {
            var toml = $"{MinimalValidToml}\n\n[voicemail]\ndelivery = \"text\"\n" +
                "max_text_recording_seconds = 30\nmax_recording_seconds = 60\n\n" +
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

    [Fact]
    public void Load_MaxRecordingSecondsAboveConfiguredTextCeiling_Succeeds()
    {
        // Raising max_text_recording_seconds above the default should let a
        // previously-rejected max_recording_seconds through.
        var modelPath = WriteTempFile("fake-model-bytes");
        try
        {
            var raisedCeiling = VoicemailBudget.MaxTextRecordingSeconds + 100;
            var toml = $"{MinimalValidToml}\n\n[voicemail]\ndelivery = \"text\"\n" +
                $"max_text_recording_seconds = {raisedCeiling}\nmax_recording_seconds = {raisedCeiling}\n\n" +
                $"[voicemail.transcription]\nmodel_path = \"{EscapeTomlString(modelPath)}\"\n";
            var path = WriteTempConfig(toml);
            try
            {
                var config = ConfigLoader.Load(path);
                Assert.Equal(raisedCeiling, config.Voicemail.MaxTextRecordingSeconds);
                Assert.Equal(raisedCeiling, config.Voicemail.MaxRecordingSeconds);
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

    [Theory]
    [InlineData("voicemail.opus")]
    [InlineData("")]
    [InlineData("{caller}.opus")]
    public void Load_RecordingFilenameWithoutTimestampOrCallId_Throws(string recordingFilename)
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nrecording_filename = \"{EscapeTomlString(recordingFilename)}\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => ConfigLoader.Load(path));
            Assert.Contains("recording_filename", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{call_id}.opus")]
    [InlineData("{caller}/{timestamp}.opus")]
    [InlineData("{TIMESTAMP}-{CALLER}.opus")]
    public void Load_RecordingFilenameWithTimestampOrCallId_Succeeds(string recordingFilename)
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nrecording_filename = \"{EscapeTomlString(recordingFilename)}\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal(recordingFilename, config.Voicemail.RecordingFilename);
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
