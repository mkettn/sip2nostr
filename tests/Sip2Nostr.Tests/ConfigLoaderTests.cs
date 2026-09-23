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
            Assert.Equal("file", config.Voicemail.Delivery);
            Assert.Equal("{timestamp}-{caller}.opus", config.Voicemail.RecordingFilename);
            Assert.Equal(VoicemailBudget.MaxRecordingSeconds, config.Voicemail.MaxRecordingSeconds);
            Assert.Equal(VoicemailBudget.OpusResamplerQuality, config.Voicemail.OpusResamplerQuality);
            Assert.Empty(config.Voicemail.BlossomServers);
            Assert.Equal(15, config.WebRtc.ConnectionLossGraceSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_DefaultSipConfig_Succeeds()
    {
        var path = WriteTempConfig(MinimalValidToml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.True(config.Sip.Tls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_SipTlsFalse_Succeeds()
    {
        var toml = $"{MinimalValidToml}\n".Replace(
            "[sip]\n",
            "[sip]\ntls = false\n");
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.False(config.Sip.Tls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_DnsSectionAbsent_Succeeds()
    {
        var path = WriteTempConfig(MinimalValidToml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Null(config.Dns);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_DnsResolversEmpty_Throws()
    {
        var toml = $"{MinimalValidToml}\n\n[dns]\nresolvers = []\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains("[dns].resolvers", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_DnsResolversWithFallback_Succeeds()
    {
        var toml = $"{MinimalValidToml}\n\n[dns]\nresolvers = [\"1.1.1.1:53\", \"9.9.9.9:53\"]\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal(["1.1.1.1:53", "9.9.9.9:53"], config.Dns!.Resolvers);
            Assert.Equal(2000, config.Dns.TimeoutMs);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("1.1.1.1:53")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("[2606:4700:4700::1111]")]
    [InlineData("[2606:4700:4700::1111]:53")]
    public void Load_DnsResolversValidFormats_Succeed(string resolver)
    {
        var toml = $"{MinimalValidToml}\n\n[dns]\nresolvers = [\"{EscapeTomlString(resolver)}\"]\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal([resolver], config.Dns!.Resolvers);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("1.1.1.1:not-a-port")]
    [InlineData("[2606:4700:4700::1111")]
    [InlineData("2606:4700:4700::1111:zz")]
    [InlineData("1.1.1.1:70000")]
    [InlineData("1.1.1.1:-1")]
    [InlineData("1.1.1.1:0")]
    public void Load_DnsResolversInvalidFormat_Throws(string resolver)
    {
        // A malformed entry (including an out-of-range port, which
        // int.TryParse alone accepts) must fail cleanly at startup
        // (ConfigurationException), not crash later inside
        // ConfiguredDnsResolver's field-initializer construction with an
        // unhandled FormatException/ArgumentOutOfRangeException.
        var toml = $"{MinimalValidToml}\n\n[dns]\nresolvers = [\"{EscapeTomlString(resolver)}\"]\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains("[dns].resolvers", exception.Message);
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
    public void Load_DefaultLoggingConsoleLevelIsWarning_Succeeds()
    {
        var path = WriteTempConfig(MinimalValidToml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal("warning", config.Logging.ConsoleLevel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_DefaultLoggingFileLevelIsInformation_Succeeds()
    {
        var path = WriteTempConfig(MinimalValidToml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal("information", config.Logging.FileLevel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_DefaultLoggingConsoleQuietIsFalse_Succeeds()
    {
        var path = WriteTempConfig(MinimalValidToml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.False(config.Logging.ConsoleQuiet);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_LoggingConsoleQuietTrue_Succeeds()
    {
        var toml = $"{MinimalValidToml}\n\n[logging]\nconsole_quiet = true\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.True(config.Logging.ConsoleQuiet);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_DefaultLoggingConsoleTimestampsIsTrue_Succeeds()
    {
        var path = WriteTempConfig(MinimalValidToml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.True(config.Logging.ConsoleTimestamps);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_LoggingConsoleTimestampsFalse_Succeeds()
    {
        var toml = $"{MinimalValidToml}\n\n[logging]\nconsole_timestamps = false\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.False(config.Logging.ConsoleTimestamps);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("verbose")]
    [InlineData("Debug")]
    [InlineData("INFORMATION")]
    [InlineData("Warning")]
    [InlineData("error")]
    [InlineData("fatal")]
    public void Load_ValidLoggingConsoleLevel_Succeeds(string level)
    {
        var toml = $"{MinimalValidToml}\n\n[logging]\nconsole_level = \"{level}\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal(level, config.Logging.ConsoleLevel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("warn")]
    [InlineData("bogus")]
    [InlineData("")]
    // Enum.TryParse alone accepts these as the numeric form of a
    // LogEventLevel value - "3" is the defined Warning, "99" isn't a
    // member at all - so name-matching (not Enum.TryParse/IsDefined) is
    // needed to reject both; "99" in particular would otherwise silently
    // produce a bridge that logs nothing, ever, since Serilog has nothing
    // at or above level 99.
    [InlineData("3")]
    [InlineData("99")]
    public void Load_InvalidLoggingConsoleLevel_Throws(string level)
    {
        var toml = $"{MinimalValidToml}\n\n[logging]\nconsole_level = \"{level}\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains("[logging].console_level", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("verbose")]
    [InlineData("Debug")]
    [InlineData("INFORMATION")]
    [InlineData("Warning")]
    [InlineData("error")]
    [InlineData("fatal")]
    public void Load_ValidLoggingFileLevel_Succeeds(string level)
    {
        var toml = $"{MinimalValidToml}\n\n[logging]\nfile_level = \"{level}\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal(level, config.Logging.FileLevel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("warn")]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("99")]
    public void Load_InvalidLoggingFileLevel_Throws(string level)
    {
        var toml = $"{MinimalValidToml}\n\n[logging]\nfile_level = \"{level}\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains("[logging].file_level", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_InvalidDeliveryValue_Throws()
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nenabled = true\ndelivery = \"bogus\"\n";
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
    public void Load_TextDeliveryWithoutModelPath_Succeeds()
    {
        // Leaving [voicemail].transcription_model_path unset when
        // delivery = "text" isn't a ConfigLoader-level mistake - it's a
        // valid choice not to set transcription up. Program.cs is what
        // reacts to it (a startup warning, falling back to
        // FileDeliveryBackend) - see docs/voicemail.md.
        var toml = $"{MinimalValidToml}\n\n[voicemail]\ndelivery = \"text\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal("text", config.Voicemail.Delivery);
            Assert.Null(config.Voicemail.TranscriptionModelPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_TextDeliveryWithMissingModelFile_Throws()
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nenabled = true\ndelivery = \"text\"\n" +
            "transcription_model_path = \"does-not-exist.bin\"\n";
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
    public void Load_TextDeliveryWithValidModelPath_Succeeds()
    {
        var modelPath = WriteTempFile("fake-model-bytes");
        try
        {
            var toml = $"{MinimalValidToml}\n\n[voicemail]\ndelivery = \"text\"\n" +
                $"transcription_model_path = \"{EscapeTomlString(modelPath)}\"\ntranscription_language = \"en\"\n";
            var path = WriteTempConfig(toml);
            try
            {
                var config = ConfigLoader.Load(path);
                Assert.Equal("text", config.Voicemail.Delivery);
                Assert.Equal("en", config.Voicemail.TranscriptionLanguage);
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
    public void Load_MaxRecordingSecondsAboveCeiling_Throws()
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nenabled = true\nmax_recording_seconds = {VoicemailBudget.MaxRecordingSecondsCeiling + 1}\n";
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

    [Fact]
    public void Load_MaxRecordingSecondsAtCeiling_Succeeds()
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nmax_recording_seconds = {VoicemailBudget.MaxRecordingSecondsCeiling}\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal(VoicemailBudget.MaxRecordingSecondsCeiling, config.Voicemail.MaxRecordingSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ConfigWithRemovedMaxTextRecordingSecondsKey_IgnoresItInsteadOfFailing()
    {
        // max_text_recording_seconds was removed when the per-mode
        // recording-length split collapsed into one max_recording_seconds
        // (see docs/voicemail.md's migration note) - this pins down what
        // actually happens to an old config that still has it, rather than
        // just asserting it in a comment: Tomlyn's TomlSerializerOptions.Default
        // (the overload ConfigLoader.Load uses) ignores an unmapped key
        // instead of failing to deserialize, so the config loads and only
        // max_recording_seconds - here left at its own default - has any
        // effect.
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nmax_text_recording_seconds = 27\n";
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

    [Fact]
    public void Load_DefaultDeliveryIsFile_Succeeds()
    {
        // "file" needs nothing beyond [voicemail].enabled - no
        // transcription model, no Blossom servers - so it's the
        // zero-setup default.
        var path = WriteTempConfig(MinimalValidToml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal("file", config.Voicemail.Delivery);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_AudioDeliveryWithoutBlossomServers_Succeeds()
    {
        // Same reasoning as Load_TextDeliveryWithoutModelPath_Succeeds:
        // an empty [voicemail].blossom_servers under delivery = "audio"
        // isn't a mistake ConfigLoader should block startup over -
        // Program.cs degrades to FileDeliveryBackend instead.
        var toml = $"{MinimalValidToml}\n\n[voicemail]\ndelivery = \"audio\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal("audio", config.Voicemail.Delivery);
            Assert.Empty(config.Voicemail.BlossomServers);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_AudioDeliveryWithBlossomServers_Succeeds()
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\ndelivery = \"audio\"\n" +
            "blossom_servers = [\"https://blossom.example.com\", \"https://blossom2.example.com\"]\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Equal("audio", config.Voicemail.Delivery);
            Assert.Equal(["https://blossom.example.com", "https://blossom2.example.com"], config.Voicemail.BlossomServers);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://blossom.example.com")]
    [InlineData("blossom.example.com")]
    public void Load_BlossomServerInvalidUrl_Throws(string server)
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nenabled = true\n" +
            $"blossom_servers = [\"{EscapeTomlString(server)}\"]\n";
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains("[voicemail].blossom_servers", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BlossomServersValidatedAsTextDeliveryFallback_Throws()
    {
        // [voicemail].blossom_servers can be configured purely as a
        // fallback under delivery = "text" - a malformed entry there
        // should still fail at startup, not just under delivery = "audio".
        var modelPath = WriteTempFile("fake-model-bytes");
        try
        {
            var toml = $"{MinimalValidToml}\n\n[voicemail]\nenabled = true\ndelivery = \"text\"\n" +
                $"transcription_model_path = \"{EscapeTomlString(modelPath)}\"\n" +
                "blossom_servers = [\"not a url\"]\n";
            var path = WriteTempConfig(toml);
            try
            {
                var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
                Assert.Contains("[voicemail].blossom_servers", exception.Message);
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
    public void Load_InvalidVoicemailTimeout_Throws(string voicemailOverride, string expectedKeyInMessage)
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nenabled = true\n{voicemailOverride}\n";
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

    [Theory]
    [InlineData("/tmp/{call_id}.opus")]
    [InlineData("../{call_id}.opus")]
    [InlineData("{caller}/../../etc/{call_id}.opus")]
    public void Load_RecordingFilenameEscapingRecordingsDir_Throws(string recordingFilename)
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nenabled = true\nrecording_filename = \"{EscapeTomlString(recordingFilename)}\"\n";
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
    [InlineData(-1)]
    [InlineData(11)]
    public void Load_OpusResamplerQualityOutOfRange_Throws(int quality)
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nenabled = true\nopus_resampler_quality = {quality}\n";
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

    [Theory]
    [InlineData("voicemail.opus")]
    [InlineData("")]
    [InlineData("{caller}.opus")]
    public void Load_RecordingFilenameWithoutTimestampOrCallId_Throws(string recordingFilename)
    {
        var toml = $"{MinimalValidToml}\n\n[voicemail]\nenabled = true\nrecording_filename = \"{EscapeTomlString(recordingFilename)}\"\n";
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

    [Fact]
    public void Load_MinimalConfigWithNostrDisabled_Succeeds()
    {
        // Unlike [sip]/[[lines]], NostrConfig's bridge_nsec/target_npub/
        // relays are deliberately NOT [TomlRequired] - they're only needed
        // when [nostr].enabled, so a config that disables Nostr shouldn't
        // have to provide even a placeholder for any of them. This is the
        // actual minimal viable config: a SIP connection plus Nostr turned
        // off, nothing else.
        var toml = """
            [sip]
            provider_host = "sip.example.com"
            username = "user"
            password = "pass"

            [[lines]]
            uri = "sip:+15551234@sip.example.com"
            label = "main"

            [nostr]
            enabled = false
            """;
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);
            Assert.Empty(config.Nostr.Relays);
            Assert.Null(config.Nostr.BridgeNsec);
            Assert.Null(config.Nostr.TargetNpub);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("bridge_nsec")]
    [InlineData("target_npub")]
    public void Load_OmittedNostrIdentityFieldWhenEnabled_Throws(string omittedKey)
    {
        // The inverse of Load_MinimalConfigWithNostrDisabled_Succeeds:
        // omitting these entirely (not just leaving them blank) still has
        // to be rejected once [nostr].enabled makes them load-bearing.
        // Removes the preceding newline, not a trailing one - target_npub
        // is NostrEnabledToml's last line, with nothing after it to eat.
        var value = omittedKey == "bridge_nsec" ? BridgeNsec : TargetNpub;
        var toml = NostrEnabledToml.Replace($"\n{omittedKey} = \"{value}\"", "");
        var path = WriteTempConfig(toml);
        try
        {
            var exception = Assert.Throws<ConfigurationException>(() => ConfigLoader.Load(path));
            Assert.Contains(omittedKey, exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_OmittedRelaysWhenNostrEnabled_Throws()
    {
        var toml = NostrEnabledToml.Replace("relays = [\"wss://relay.example.com\"]\n", "");
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
