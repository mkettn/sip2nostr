using Serilog;
using Sip2Nostr.Config;
using Tmds.DBus;

namespace Sip2Nostr.Modem;

// One incoming call reported by ModemManager, handed off to ModemCallSource.
// `Call` is the D-Bus proxy for Accept/Hangup and for watching further
// state changes.
public sealed record ModemIncomingCall(ICall Call, ObjectPath CallPath, string? Number);

// Talks to ModemManager (https://www.freedesktop.org/software/ModemManager/)
// over the system D-Bus to find a directly attached phone/modem device and
// watch it for inbound calls - the D-Bus equivalent of SipCallSource's SIP
// registration + OnIncomingCall for a locally attached device instead of a
// SIP trunk.
public sealed class ModemManagerClient(ModemConfig config, ILogger logger) : IAsyncDisposable
{
    private const string ServiceName = "org.freedesktop.ModemManager1";
    private const string ManagerObjectPath = "/org/freedesktop/ModemManager1";
    private const string VoiceInterfaceName = "org.freedesktop.ModemManager1.Modem.Voice";

    private Connection? _connection;
    private IModemVoice? _voice;
    private IDisposable? _interfacesAddedWatch;
    private IDisposable? _callAddedWatch;

    public event Action<ModemIncomingCall>? OnIncomingCall;

    public async Task StartAsync(CancellationToken ct)
    {
        _connection = Connection.System;
        var manager = _connection.CreateProxy<IObjectManager>(ServiceName, ManagerObjectPath);

        if (!string.IsNullOrWhiteSpace(config.ModemObjectPath))
        {
            logger.Information("Using configured ModemManager modem object path {ModemPath}.", config.ModemObjectPath);
            await AttachToModemAsync(new ObjectPath(config.ModemObjectPath));
            return;
        }

        var managedObjects = await manager.GetManagedObjectsAsync();
        var modemPath = FindVoiceModemPath(managedObjects);
        if (modemPath is not null)
        {
            await AttachToModemAsync(modemPath.Value);
            return;
        }

        logger.Warning(
            "No ModemManager modem with voice-call support found yet for line {Label}; waiting for one to attach.",
            config.Label);
        _interfacesAddedWatch = await manager.WatchInterfacesAddedAsync(
            args => _ = OnInterfacesAddedSafeAsync(args.ObjectPath, args.Interfaces),
            exception => logger.Warning(exception, "ModemManager InterfacesAdded watch failed."));
    }

    // Pure so it can be exercised without a live D-Bus connection: picks the
    // first object exposing the Voice interface, matching ModemManager's
    // /org/freedesktop/ModemManager1/Modem/<N> layout.
    internal static ObjectPath? FindVoiceModemPath(
        IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>> managedObjects)
    {
        foreach (var (objectPath, interfaces) in managedObjects)
        {
            if (interfaces.ContainsKey(VoiceInterfaceName))
            {
                return objectPath;
            }
        }

        return null;
    }

    private async Task OnInterfacesAddedSafeAsync(
        ObjectPath objectPath,
        IDictionary<string, IDictionary<string, object>> interfaces)
    {
        try
        {
            if (_voice is not null || !interfaces.ContainsKey(VoiceInterfaceName))
            {
                return;
            }

            logger.Information("ModemManager modem with voice-call support attached at {ModemPath}.", objectPath);
            await AttachToModemAsync(objectPath);
        }
        catch (Exception exception)
        {
            logger.Error(exception, "Failed to attach to newly added ModemManager modem at {ModemPath}.", objectPath);
        }
    }

    private async Task AttachToModemAsync(ObjectPath modemPath)
    {
        _voice = _connection!.CreateProxy<IModemVoice>(ServiceName, modemPath);
        _callAddedWatch = await _voice.WatchCallAddedAsync(
            callPath => _ = OnCallAddedSafeAsync(callPath),
            exception => logger.Warning(exception, "ModemManager CallAdded watch failed."));
        logger.Information("Listening for inbound calls on ModemManager modem {ModemPath} (line {Label}).", modemPath, config.Label);

        var existingCalls = await _voice.ListCallsAsync();
        foreach (var callPath in existingCalls)
        {
            await OnCallAddedSafeAsync(callPath);
        }
    }

    private async Task OnCallAddedSafeAsync(ObjectPath callPath)
    {
        try
        {
            var call = _connection!.CreateProxy<ICall>(ServiceName, callPath);
            var direction = await call.GetAsync<int>("Direction");
            if (direction != MMCallDirection.Incoming)
            {
                logger.Debug("Ignoring non-incoming ModemManager call at {CallPath} (direction {Direction}).", callPath, direction);
                return;
            }

            string? number = null;
            try
            {
                number = await call.GetAsync<string>("Number");
            }
            catch (Exception exception)
            {
                logger.Debug(exception, "Could not read Number for ModemManager call at {CallPath}.", callPath);
            }

            logger.Information(
                "Incoming modem call from {Number} on {CallPath}; line: {LineLabel}.",
                number ?? "(unknown)",
                callPath,
                config.Label);
            OnIncomingCall?.Invoke(new ModemIncomingCall(call, callPath, number));
        }
        catch (Exception exception)
        {
            logger.Error(exception, "Failed to handle new ModemManager call at {CallPath}.", callPath);
        }
    }

    public ValueTask DisposeAsync()
    {
        _callAddedWatch?.Dispose();
        _interfacesAddedWatch?.Dispose();
        return ValueTask.CompletedTask;
    }
}
