using Nostr.Sdk;
using Serilog;
using Serilog.Events;
using Sip2Nostr.Config;
using Sip2Nostr.Hub;
using Sip2Nostr.Signaling;
using Sip2Nostr.Sinks;
using Sip2Nostr.Sip;
using Sip2Nostr.Voicemail;

const string LogOutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";
const string LogOutputTemplateNoTimestamp = "[{Level:u3}] {Message:lj}{NewLine}{Exception}";

Log.Logger = CreateLogger(null, Directory.GetCurrentDirectory(), out _);

static Serilog.Core.Logger CreateLogger(
    LoggingConfig? logging,
    string configDirectory,
    out string? runLogPath)
{
    runLogPath = ResolveRunLogPath(logging?.RunFile, configDirectory);

    // ConfigLoader.Validate already rejected anything but a real
    // Serilog level name by the time this runs with a loaded config;
    // the one call site that doesn't have one yet - the bootstrap logger
    // created before config is even read, logging: null - falls back to
    // the same "warning" default LoggingConfig itself uses, so log output
    // before and after config load is governed by the same default.
    var level = Enum.TryParse<LogEventLevel>(logging?.Level, ignoreCase: true, out var parsedLevel)
        ? parsedLevel
        : LogEventLevel.Warning;

    // The console is restricted to `level` directly, but the global
    // minimum (the floor every sink shares, including the run log file)
    // stays at least Information whenever a run file is configured: a
    // fresh timestamped log per run exists specifically for after-the-fact
    // troubleshooting, so it shouldn't come up empty for a run that looked
    // fine at the time but wasn't - by the point you're reaching for it,
    // "turn the level down and reproduce it" often isn't an option. A
    // `level` more verbose than Information (e.g. "debug") still wins,
    // since Serilog's MinimumLevel is a hard floor no sink's own
    // restrictedToMinimumLevel can widen back past.
    var globalLevel = runLogPath is not null && level > LogEventLevel.Information
        ? LogEventLevel.Information
        : level;

    // Console-only, and defaulting to true (unlike Level/Quiet, both
    // false-by-default): most direct/interactive runs want the timestamp,
    // it's specifically a supervisor that already stamps captured output
    // - systemd/journald being the common case - that wants it turned off,
    // to stop each line showing two timestamps instead of one. run_file
    // always keeps its own timestamp regardless - see LoggingConfig.
    var consoleTemplate = (logging?.ConsoleTimestamps ?? true) ? LogOutputTemplate : LogOutputTemplateNoTimestamp;

    var logger = new LoggerConfiguration()
        .MinimumLevel.Is(globalLevel)
        .WriteTo.Console(restrictedToMinimumLevel: level, outputTemplate: consoleTemplate);

    if (runLogPath is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(runLogPath)!);
        logger.WriteTo.File(runLogPath, outputTemplate: LogOutputTemplate, shared: true);
    }

    return logger.CreateLogger();
}

// Only loads a transcriber (and its GGML model) when it'll actually be
// used - voicemail disabled, or delivery = "file" (the default), stays
// as cheap to start up as before this existed. Neither "audio" nor
// "text"'s own requirement being configured is a startup failure (see
// ConfigLoader.Validate) - it degrades to FileDeliveryBackend with a
// warning instead, so a voicemail still gets saved to recordings_dir and
// the caller still gets a notice, just without the recording/transcript
// itself. See docs/voicemail.md.
static IVoicemailDeliveryBackend CreateVoicemailDeliveryBackend(AppConfig config, ILogger logger)
{
    if (!config.Voicemail.Enabled)
    {
        return new FileDeliveryBackend();
    }

    if (config.Voicemail.Delivery == "text")
    {
        if (string.IsNullOrWhiteSpace(config.Voicemail.Transcription.ModelPath))
        {
            logger.Warning(
                "[voicemail].delivery is \"text\" but [voicemail.transcription].model_path is not set; " +
                "voicemails will be saved to recordings_dir only, not delivered over Nostr.");
            return new FileDeliveryBackend();
        }

        return CreateTextDeliveryBackend(config, logger);
    }

    if (config.Voicemail.Delivery == "audio")
    {
        if (config.Voicemail.Blossom.Servers.Count == 0)
        {
            logger.Warning(
                "[voicemail].delivery is \"audio\" but [voicemail.blossom].servers is empty; " +
                "voicemails will be saved to recordings_dir only, not delivered over Nostr.");
            return new FileDeliveryBackend();
        }

        return CreateAudioDeliveryBackend(config, logger);
    }

    // "file" - ConfigLoader.Validate already restricted Delivery to
    // "file"/"audio"/"text", so this is the only value left.
    return new FileDeliveryBackend();
}

static AudioDeliveryBackend CreateAudioDeliveryBackend(AppConfig config, ILogger logger)
{
    var servers = config.Voicemail.Blossom.Servers.Select(s => new Uri(s)).ToList();
    return new AudioDeliveryBackend(servers, config.Nostr, logger.ForContext<AudioDeliveryBackend>());
}

// The audio fallback (see Voicemail/TranscribedTextDeliveryBackend.cs) is
// only wired up when [voicemail.blossom].servers is actually configured -
// otherwise a transcription failure keeps its own plain-text-notice
// fallback, not a new dependency nobody asked for.
static TranscribedTextDeliveryBackend CreateTextDeliveryBackend(AppConfig config, ILogger logger)
{
    var transcriber = CreateTranscriber(config.Voicemail.Transcription, config.ConfigDirectory, logger);
    IVoicemailDeliveryBackend? audioFallback = config.Voicemail.Blossom.Servers.Count > 0
        ? CreateAudioDeliveryBackend(config, logger)
        : null;
    return new TranscribedTextDeliveryBackend(transcriber, audioFallback, logger.ForContext<TranscribedTextDeliveryBackend>());
}

static IVoicemailTranscriber CreateTranscriber(TranscriptionConfig transcriptionConfig, string configDirectory, ILogger logger)
{
    var modelPath = Path.IsPathRooted(transcriptionConfig.ModelPath!)
        ? transcriptionConfig.ModelPath!
        : Path.GetFullPath(Path.Combine(configDirectory, transcriptionConfig.ModelPath!));

    return transcriptionConfig.Engine switch
    {
        "whisper" => new WhisperNetTranscriber(modelPath, transcriptionConfig.Language, logger.ForContext<WhisperNetTranscriber>()),
        _ => throw new InvalidOperationException($"Unknown [voicemail.transcription] engine '{transcriptionConfig.Engine}'."),
    };
}

static string? ResolveRunLogPath(string? runFile, string configDirectory)
{
    if (string.IsNullOrWhiteSpace(runFile))
    {
        return null;
    }

    var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
    var path = runFile
        .Replace("{timestamp}", timestamp, StringComparison.OrdinalIgnoreCase)
        .Replace("{run}", timestamp, StringComparison.OrdinalIgnoreCase);

    if (!path.Contains(timestamp, StringComparison.Ordinal))
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        path = Path.Combine(directory ?? "", $"{fileName}-{timestamp}{extension}");
    }

    return Path.GetFullPath(Path.IsPathRooted(path)
        ? path
        : Path.Combine(configDirectory, path));
}

try
{
    var configPath = args.Length > 0 ? args[0] : "config.toml";

    Log.Information("Loading configuration from {ConfigPath}.", configPath);
    var config = ConfigLoader.Load(configPath);
    Log.Logger = CreateLogger(config.Logging, config.ConfigDirectory, out var runLogPath);
    if (runLogPath is not null)
    {
        Log.Information("Writing this run's log to {RunLogPath}.", runLogPath);
    }

    // Only meaningful (and only validated - see ConfigLoader) when Nostr
    // is actually in use: the local SIP-test path ([nostr].enabled =
    // false, see docs/receiving-calls.md) never touches bridge_nsec at
    // all, and needs no Nostr identity to run.
    if (config.Nostr.Enabled)
    {
        // Non-null here: ConfigLoader.ValidateBridgeIdentity already
        // rejected a null/blank bridge_nsec whenever [nostr].enabled - the
        // property itself is nullable only because it's optional when
        // disabled.
        var bridgeNpub = Keys.Parse(config.Nostr.BridgeNsec!).PublicKey().ToBech32();
        Log.Information("Bridge Nostr identity: {BridgeNpub}", bridgeNpub);
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Log.Information("Shutdown requested.");
        cts.Cancel();
    };

    var voicemailDeliveryBackend = CreateVoicemailDeliveryBackend(config, Log.Logger);
    await using var voicemailSender = new VoicemailSender(config.Nostr, config.Voicemail, voicemailDeliveryBackend, Log.Logger.ForContext<VoicemailSender>());

    var sinks = new List<ICallSink>();
    if (config.Nostr.Enabled)
    {
        sinks.Add(new NosCallSink(
            config.Nostr,
            config.WebRtc,
            config.Voicemail.Enabled ? config.Voicemail.RingTimeoutSeconds : null,
            Log.Logger.ForContext<NosCallSink>()));

        if (config.Voicemail.Enabled)
        {
            sinks.Add(new VoicemailSink(
                config.Voicemail,
                voicemailSender,
                voicemailDeliveryBackend.RequiresPcm,
                config.ConfigDirectory,
                Log.Logger.ForContext<VoicemailSink>()));
        }

        // Fatal, not fire-and-forget: without at least one reachable
        // relay, sip2nostr can't bridge a call at all, so this is awaited
        // before SIP registration starts rather than left to surface a
        // Warning sometime after the process is already "running" - see
        // NostrSignalingClient.CheckConnectivityAsync.
        await NostrSignalingClient.CheckConnectivityAsync(config.Nostr, Log.Logger.ForContext<NostrSignalingClient>());
    }
    else
    {
        sinks.Add(new LocalTestAudioSink(config.Lines, config.ConfigDirectory, Log.Logger.ForContext<LocalTestAudioSink>()));
    }

    var hub = new CallHub(sinks, Log.Logger.ForContext<CallHub>());

    await using var source = new SipCallSource(config, Log.Logger.ForContext<SipCallSource>());
    hub.Attach(source, cts.Token);
    await source.StartAsync(cts.Token);

    // Plain stdout, not a log event: this is a one-time confirmation for
    // whoever's watching a foreground terminal, not something
    // [logging].level should be able to filter out the way it does actual
    // log events (see LoggingConfig.Quiet) - a supervised/scripted run
    // opts out via [logging].quiet instead.
    if (!config.Logging.Quiet)
    {
        Console.WriteLine("sip2nostr running. Press Ctrl+C to exit.");
    }

    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // Shutting down.
    }

    // Runs before source/voicemailSender are disposed below, and before
    // the log flush in the outer finally, so a call that's mid-hangup or
    // mid-recording when shutdown starts gets a real chance to finish
    // rather than being cut off the instant cts.Cancel() fires - see
    // CallHub.DrainAsync and docs/propagating-to-nostr.md.
    await hub.DrainAsync(TimeSpan.FromSeconds(5));
}
catch (ConfigurationException exception)
{
    // A bad config value or a busy port is the operator's to fix, not a
    // bug - a single clean line says so; the stack trace below would only
    // bury that under noise. See ConfigurationException.
    Log.Fatal("{Message}", exception.Message);
    Environment.ExitCode = 1;
}
catch (Exception exception)
{
    Log.Fatal(exception, "sip2nostr terminated unexpectedly.");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
