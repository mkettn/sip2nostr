# sip2nostr

A gateway that answers incoming calls on an existing VoIP SIP account and
forwards them as a call-signaling request over Nostr (NIP-AC) to a
compatible client (e.g. NosCall), bridging audio between the SIP/RTP leg
and a WebRTC leg. It replaces a softphone as the thing registered to your
VoIP provider: an inbound call rings your Nostr identity instead of a
phone. Configuration is a single TOML file - no UI.

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
(the target machine only needs the runtime, `dotnet-runtime-8.0`, to run
it). Targets linux-x64 (amd64) and linux-arm64 (e.g. Raspberry Pi 4/5 on
the 64-bit OS) - `build.sh` picks the right one automatically.

```
./build.sh
```

## Running

Copy `config.example.toml` to a `config.toml` of your choosing and fill
in your SIP and Nostr credentials. Then run the build directly - no
installation required:

```
out/<linux-x64|linux-arm64>/Sip2Nostr config.toml
```

Or from source: `cd src/Sip2Nostr && dotnet run -- ../../config.toml`.

## Installing as a systemd service (optional)

For a long-running deployment:

```
sudo ./install.sh
sudo nano /var/local/lib/sip2nostr/config.toml
sudo systemctl enable --now sip2nostr
```

See `docs/` for the caller allow/deny-list, voicemail fallback, and
sound-file format.
