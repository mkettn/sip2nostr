using Sip2Nostr.Modem;
using Tmds.DBus;
using Xunit;

namespace Sip2Nostr.Tests;

public class ModemManagerClientTests
{
    [Fact]
    public void FindVoiceModemPath_ReturnsObjectExposingVoiceInterface()
    {
        var modemPath = new ObjectPath("/org/freedesktop/ModemManager1/Modem/0");
        var managedObjects = new Dictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>
        {
            [new ObjectPath("/org/freedesktop/ModemManager1")] = new Dictionary<string, IDictionary<string, object>>
            {
                ["org.freedesktop.DBus.ObjectManager"] = new Dictionary<string, object>(),
            },
            [modemPath] = new Dictionary<string, IDictionary<string, object>>
            {
                ["org.freedesktop.ModemManager1.Modem"] = new Dictionary<string, object>(),
                ["org.freedesktop.ModemManager1.Modem.Voice"] = new Dictionary<string, object>(),
            },
        };

        var found = ModemManagerClient.FindVoiceModemPath(managedObjects);

        Assert.Equal(modemPath, found);
    }

    [Fact]
    public void FindVoiceModemPath_ReturnsNullWhenNoModemHasVoice()
    {
        var managedObjects = new Dictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>
        {
            [new ObjectPath("/org/freedesktop/ModemManager1/Modem/0")] = new Dictionary<string, IDictionary<string, object>>
            {
                ["org.freedesktop.ModemManager1.Modem"] = new Dictionary<string, object>(),
            },
        };

        var found = ModemManagerClient.FindVoiceModemPath(managedObjects);

        Assert.Null(found);
    }
}
