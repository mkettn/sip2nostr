using Sip2Nostr.Config;
using Sip2Nostr.Shared;
using Xunit;

namespace Sip2Nostr.Tests;

public class ConfigLoaderTests
{
    // Arbitrary, freshly generated for this test file only - not tied to
    // any real Nostr identity or relay. Needs to actually parse (unlike
    // the old "nsec1...""/"npub1..." placeholders) now that ConfigLoader
    // validates them eagerly - see issue #26.
    private const string BridgeNsec = "nsec1wlhduv429l0ggtp36zqv9jm767898l40hd62kyspgclhklzdthss37hrv3";
    private const string TargetNpub = "npub1mn44lshdqvxmrulx0xveghc4w0jwwl6dxl5vd8hvayt342ehxcrssmgts5";

    // [nostr].enabled = false here on purpose: target_npub/relays/dm_relays
    // and [[lines]].sound are only ever read when Nostr is enabled (or, for
    // greeting_sound, when voicemail is also enabled - see Program.cs), so
    // ConfigLoader only validates their *format* in that case; this fixture
    // exercises everything that's validated regardless (bridge_nsec, the
    // voicemail numeric/filename checks, [[lines]].sound). NostrEnabledToml
    // below covers the gated checks.
    private const string MinimalValidToml = $"""
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
        bridge_nsec = "{BridgeNsec}"
        target_npub = "{TargetNpub}"
        """;

    private const string NostrEnabledToml = $"""
        [sip]
        provider_host = "sip.example.com"
        username = "user"
        password = "pass"

        [[lines]]
        uri = "sip:+15551234@sip.example.com"
        label = "main"

        [nostr]
        enabled = true
        relays = ["wss://relay.example.com"]
        bridge_nsec = "{BridgeNsec}"
        target_npub = "{TargetNpub}"
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
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
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
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
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
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
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
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
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
                var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
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
                var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
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
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
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
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
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
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
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
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
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
                var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
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
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
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

    [Fact]
    public void Load_MissingConfigFile_ThrowsConfigurationException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sip2nostr-test-{Guid.NewGuid():N}.toml");
        var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
        Assert.Contains(path, exception.Message);
    }

    [Fact]
    public void Load_MalformedToml_ThrowsConfigurationException()
    {
        var path = WriteTempConfig("[sip\nbroken");
        try
        {
            Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("bridge_nsec = \"\"", "bridge_nsec")]
    [InlineData("bridge_nsec = \"not-a-valid-key\"", "bridge_nsec")]
    public void Load_InvalidBridgeNsec_Throws(string bridgeNsecLine, string expectedKeyInMessage)
    {
        // bridge_nsec is only validated when [nostr].enabled - same
        // reasoning as target_npub/relays below: Program.cs only parses it
        // (to log the bridge's npub) in that case too, so this needs
        // NostrEnabledToml, not the enabled = false fixture. .Replace, not
        // append: appending a second bridge_nsec under the same [nostr]
        // table is a duplicate TOML key and fails at the parse step, never
        // reaching the check this test means to exercise.
        var toml = NostrEnabledToml.Replace($"bridge_nsec = \"{BridgeNsec}\"", bridgeNsecLine);
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains(expectedKeyInMessage, exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_InvalidBridgeNsecWhenNostrDisabled_Succeeds()
    {
        // The documented [nostr].enabled = false local SIP-test path
        // (docs/receiving-calls.md) never touches bridge_nsec either -
        // Program.cs skips logging the bridge's npub in that mode too - so
        // a garbage value there shouldn't block startup. Copying
        // config.example.toml's bridge_nsec = "nsec1..." placeholder
        // verbatim into that mode is exactly this case.
        var toml = MinimalValidToml.Replace($"bridge_nsec = \"{BridgeNsec}\"", "bridge_nsec = \"nsec1...\"");
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal("nsec1...", config.Nostr.BridgeNsec);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("target_npub = \"\"", "target_npub")]
    [InlineData("target_npub = \"not-a-valid-key\"", "target_npub")]
    public void Load_InvalidTargetNpub_Throws(string targetNpubLine, string expectedKeyInMessage)
    {
        // target_npub is only validated when [nostr].enabled - see
        // NostrEnabledToml's own comment.
        var toml = NostrEnabledToml.Replace($"target_npub = \"{TargetNpub}\"", targetNpubLine);
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains(expectedKeyInMessage, exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_InvalidTargetNpubWhenNostrDisabled_Succeeds()
    {
        // The documented [nostr].enabled = false local SIP-test path
        // (docs/receiving-calls.md) never touches target_npub - NosCallSink
        // isn't even constructed - so a garbage value there shouldn't block
        // startup.
        var toml = MinimalValidToml.Replace($"target_npub = \"{TargetNpub}\"", "target_npub = \"not-a-valid-key\"");
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal("not-a-valid-key", config.Nostr.TargetNpub);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_EmptyRelays_Throws()
    {
        var toml = NostrEnabledToml.Replace("relays = [\"wss://relay.example.com\"]", "relays = []");
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains("relays", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("relays = [\"not a url\"]", "relays")]
    [InlineData("relays = [\"http://relay.example.com\"]", "relays")]
    public void Load_InvalidRelayUrl_Throws(string relaysLine, string expectedKeyInMessage)
    {
        var toml = NostrEnabledToml.Replace("relays = [\"wss://relay.example.com\"]", relaysLine);
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains(expectedKeyInMessage, exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_InvalidDmRelay_Throws()
    {
        var toml = $"{NostrEnabledToml}\n\n[voicemail]\ndm_relays = [\"not a url\"]\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains("dm_relays", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ValidDmRelay_Succeeds()
    {
        var toml = $"{NostrEnabledToml}\n\n[voicemail]\ndm_relays = [\"wss://dm-relay.example.com\"]\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal(["wss://dm-relay.example.com"], config.Voicemail.DmRelays);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingGreetingSoundFile_Throws()
    {
        // greeting_sound only matters to VoicemailSink, wired in only when
        // both [nostr] and [voicemail] are enabled - see Program.cs.
        var toml = $"{NostrEnabledToml}\n\n[voicemail]\nenabled = true\ngreeting_sound = \"does-not-exist.opus\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains("greeting_sound", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_GreetingSoundUnusedWhenVoicemailDisabled_Succeeds()
    {
        // [voicemail].enabled defaults to false - a broken greeting_sound
        // shouldn't block startup when VoicemailSink is never constructed.
        var toml = $"{NostrEnabledToml}\n\n[voicemail]\ngreeting_sound = \"does-not-exist.opus\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal("does-not-exist.opus", config.Voicemail.GreetingSound);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_UnsupportedGreetingSoundFormat_Throws()
    {
        var soundPath = WriteTempFile("not-a-sound-file", ".mp3");
        try
        {
            var toml = $"{NostrEnabledToml}\n\n[voicemail]\nenabled = true\ngreeting_sound = \"{EscapeTomlString(soundPath)}\"\n";
            var path = WriteTempConfig(toml);
            try
            {
                var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
                Assert.Contains("greeting_sound", exception.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }
        finally
        {
            File.Delete(soundPath);
        }
    }

    [Fact]
    public void Load_ValidGreetingSoundFile_Succeeds()
    {
        var soundPath = WriteTempFile("raw-pcm-bytes", ".pcm");
        try
        {
            var toml = $"{NostrEnabledToml}\n\n[voicemail]\nenabled = true\ngreeting_sound = \"{EscapeTomlString(soundPath)}\"\n";
            var path = WriteTempConfig(toml);
            try
            {
                var config = ConfigLoader.Load(path);
                Assert.Equal(soundPath, config.Voicemail.GreetingSound);
            }
            finally
            {
                File.Delete(path);
            }
        }
        finally
        {
            File.Delete(soundPath);
        }
    }

    [Fact]
    public void Load_MissingLineSoundFile_ThrowsWithLineLabel()
    {
        // [[lines]].sound only matters to LocalTestAudioSink, wired in only
        // when [nostr] is disabled - see Program.cs - so this uses
        // MinimalValidToml (enabled = false), not NostrEnabledToml.
        var toml = MinimalValidToml.Replace(
            "label = \"main\"",
            "label = \"main\"\nsound = \"does-not-exist.opus\"");
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains("main", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_InvalidLineSoundWhenNostrEnabled_Succeeds()
    {
        // The inverse of Load_MissingLineSoundFile_ThrowsWithLineLabel:
        // LocalTestAudioSink never runs when [nostr] is enabled, so a
        // broken [[lines]].sound shouldn't block startup in that mode.
        var toml = NostrEnabledToml.Replace(
            "label = \"main\"",
            "label = \"main\"\nsound = \"does-not-exist.opus\"");
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal("does-not-exist.opus", config.Lines[0].Sound);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingNostrSection_ThrowsConfigurationException()
    {
        // AppConfig.Nostr is [TomlRequired] precisely so a missing [nostr]
        // section fails here, cleanly, instead of ConfigLoader
        // dereferencing a null config.Nostr with a NullReferenceException.
        var toml = """
            [sip]
            provider_host = "sip.example.com"
            username = "user"
            password = "pass"

            [[lines]]
            uri = "sip:+15551234@sip.example.com"
            label = "main"
            """;
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains("nostr", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingRequiredSipField_ThrowsConfigurationException()
    {
        // Same [TomlRequired] mechanism, on a nested required string field
        // rather than a whole required section.
        var toml = MinimalValidToml.Replace("username = \"user\"\n", "");
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains("username", exception.Message);
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

    private static string WriteTempFile(string content, string extension = ".bin")
    {
        var path = Path.Combine(Path.GetTempPath(), $"sip2nostr-test-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content);
        return path;
    }

    private static string EscapeTomlString(string value) => value.Replace(@"\", @"\\");
}
