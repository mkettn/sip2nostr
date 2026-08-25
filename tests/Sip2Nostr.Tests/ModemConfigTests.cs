using Sip2Nostr.Config;
using Xunit;

namespace Sip2Nostr.Tests;

public class ModemConfigTests
{
    private const string BaseToml =
        "[sip]\n" +
        "provider_host = \"sip.example.com\"\n" +
        "username = \"user\"\n" +
        "password = \"pass\"\n" +
        "\n" +
        "[[lines]]\n" +
        "uri = \"sip:+10000000000@sip.example.com\"\n" +
        "label = \"main\"\n" +
        "\n" +
        "[nostr]\n" +
        "enabled = false\n" +
        "relays = []\n" +
        "bridge_nsec = \"nsec1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq\"\n" +
        "target_npub = \"npub1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq\"\n";

    [Fact]
    public void Load_ModemSectionAbsent_ModemIsNull()
    {
        var path = WriteTempConfig(BaseToml);
        try
        {
            var config = ConfigLoader.Load(path);

            Assert.Null(config.Modem);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ModemSectionPresent_ParsesFields()
    {
        var toml = BaseToml +
            "\n[modem]\n" +
            "enabled = true\n" +
            "alsa_device = \"hw:1,0\"\n" +
            "label = \"mobile\"\n";
        var path = WriteTempConfig(toml);
        try
        {
            var config = ConfigLoader.Load(path);

            Assert.NotNull(config.Modem);
            Assert.True(config.Modem!.Enabled);
            Assert.Equal("hw:1,0", config.Modem.AlsaDevice);
            Assert.Equal("mobile", config.Modem.Label);
            Assert.Null(config.Modem.ModemObjectPath);
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
