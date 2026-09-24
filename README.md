<img src="assets/logo.svg" width="120" height="120" alt="sip2nostr logo">

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

## Voicemail recordings on a shared volume

If `[voicemail].recordings_dir` points outside `/var/local/lib/sip2nostr`
- a NAS mount shared with other services, say,
`/shared/voicemail_recordings` - `ProtectSystem=strict` in the unit
blocks writes there until you add it explicitly. Use a drop-in rather
than editing the shipped unit file:

```
sudo systemctl edit sip2nostr
```

```ini
[Service]
ReadWritePaths=/shared/voicemail_recordings
```

and make sure the `sip2nostr` user can actually write there - add it to
whatever group owns the share and `chmod g+w` the directory, or `chown`
it directly. `sysusers.d/sip2nostr.conf` only creates the `sip2nostr`
user/group themselves; group membership on a shared external path is
deployment-specific and left to you.


## Uninstalling

```
sudo systemctl disable --now sip2nostr
sudo rm -rf /usr/local/lib/sip2nostr /usr/local/lib/sysusers.d/sip2nostr.conf /usr/local/lib/systemd/system/sip2nostr.service
sudo systemctl daemon-reload
# also deletes config.toml and any voicemail recordings:
sudo rm -rf /var/local/lib/sip2nostr
sudo userdel sip2nostr
```
