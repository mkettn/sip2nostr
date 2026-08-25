using Tmds.DBus;

namespace Sip2Nostr.Modem;

// Client-side D-Bus proxy interfaces for the subset of ModemManager's API
// (https://www.freedesktop.org/software/ModemManager/doc/latest/ModemManager/ref-dbus.html)
// this source needs: finding a modem, watching for inbound calls, and
// accepting/hanging them up. Signatures are taken directly from
// ModemManager's introspection XML (org.freedesktop.ModemManager1.Modem.Voice.xml,
// org.freedesktop.ModemManager1.Call.xml), not guessed.
//
// Call audio itself is never carried over D-Bus - ModemManager only
// controls call state - so there is no audio-related member here; see
// AlsaPcmDevice for the actual audio path.

[DBusInterface("org.freedesktop.DBus.ObjectManager")]
public interface IObjectManager : IDBusObject
{
    Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjectsAsync();

    Task<IDisposable> WatchInterfacesAddedAsync(
        Action<(ObjectPath ObjectPath, IDictionary<string, IDictionary<string, object>> Interfaces)> handler,
        Action<Exception>? onError = null);
}

[DBusInterface("org.freedesktop.ModemManager1.Modem.Voice")]
public interface IModemVoice : IDBusObject
{
    Task<ObjectPath[]> ListCallsAsync();

    Task<IDisposable> WatchCallAddedAsync(Action<ObjectPath> handler, Action<Exception>? onError = null);
}

[DBusInterface("org.freedesktop.ModemManager1.Call")]
public interface ICall : IDBusObject
{
    Task AcceptAsync();

    Task HangupAsync();

    Task<T> GetAsync<T>(string prop);

    Task<IDisposable> WatchStateChangedAsync(
        Action<(int Old, int New, uint Reason)> handler,
        Action<Exception>? onError = null);
}

// MMCallState (ModemManager-enums.h) - state of a Call object.
public static class MMCallState
{
    public const int Unknown = 0;
    public const int Dialing = 1;
    public const int RingingOut = 2;
    public const int RingingIn = 3;
    public const int Active = 4;
    public const int Held = 5;
    public const int Waiting = 6;
    public const int Terminated = 7;
}

// MMCallDirection (ModemManager-enums.h).
public static class MMCallDirection
{
    public const int Unknown = 0;
    public const int Incoming = 1;
    public const int Outgoing = 2;
}
