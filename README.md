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
the 64-bit OS) - `build.sh` picks the right one for the current machine
automatically.

```
./build.sh
```

## Installing

```
sudo ./install.sh
```

Installs to `/usr/local/lib/sip2nostr/` and symlinks
`/usr/local/bin/sip2nostr` to it. To uninstall:

```
rm -rf /usr/local/lib/sip2nostr /usr/local/bin/sip2nostr
```

## Configuring

Copy `config.example.toml` to a `config.toml` of your choosing and fill
in your SIP and Nostr credentials - every option is documented inline
there. See `docs/` for the caller allow/deny-list, voicemail fallback,
and sound-file format in more detail.

## Running

```
sip2nostr /path/to/config.toml
```

Or, from source:

```
cd src/Sip2Nostr
dotnet run -- /path/to/config.toml
```
