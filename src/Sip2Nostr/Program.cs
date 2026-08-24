using Nostr.Sdk;
using Serilog;
using Sip2Nostr.Config;
using Sip2Nostr.Sip;

const string LogOutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

Log.Logger = CreateLogger(null, Directory.GetCurrentDirectory(), out _);

static Serilog.Core.Logger CreateLogger(
    LoggingConfig? logging,
    string configDirectory,
    out string? runLogPath)
{
    runLogPath = ResolveRunLogPath(logging?.RunFile, configDirectory);

    var logger = new LoggerConfiguration()
    .MinimumLevel.Information()
        .WriteTo.Console(outputTemplate: LogOutputTemplate);

    if (runLogPath is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(runLogPath)!);
        logger.WriteTo.File(runLogPath, outputTemplate: LogOutputTemplate, shared: true);
    }

    return logger.CreateLogger();
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

    // Printed regardless of [nostr].enabled - bridge_nsec is always
    // required, and knowing this npub is what lets someone add the
    // bridge as a contact in their receiving client (see README).
    var bridgeNpub = Keys.Parse(config.Nostr.BridgeNsec).PublicKey().ToBech32();
    Log.Information("Bridge Nostr identity: {BridgeNpub}", bridgeNpub);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Log.Information("Shutdown requested.");
        cts.Cancel();
    };

    await using var bridge = new BridgeService(config, Log.Logger.ForContext<BridgeService>());
    await bridge.StartAsync(cts.Token);

    Log.Information("sip2nostr running. Press Ctrl+C to exit.");

    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
        // Shutting down.
    }
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
