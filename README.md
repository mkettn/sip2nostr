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

## Running as a systemd service

`systemd/` ships a hardened unit and a `sysusers.d` file that create and
confine a dedicated `sip2nostr` user. The unit's `StateDirectory=`
creates `/var/lib/sip2nostr` - the only place the service can write -
owned by `sip2nostr`, mode `0700`, recreated with that same ownership
and mode on every start if it's ever missing. `config.toml` (your SIP
password and `bridge_nsec`) goes there too, so create it ahead of the
first start:

```
sudo cp systemd/sysusers.d/sip2nostr.conf /etc/sysusers.d/
sudo systemd-sysusers
sudo install -d -o sip2nostr -g sip2nostr -m 0700 /var/lib/sip2nostr
sudo install -o sip2nostr -g sip2nostr -m 0600 config.toml /var/lib/sip2nostr/config.toml
sudo cp systemd/sip2nostr.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now sip2nostr
```

Put anything else `config.toml` is set to read locally in that same
directory too: `[[lines]].sound`/`[voicemail].greeting_sound` files,
`[voicemail].transcription_model_path`'s GGML model, and - unless
redirected elsewhere, see below - `[voicemail].recordings_dir`.

journald already captures and timestamps everything sip2nostr prints, so
leave `[logging].file` unset in `config.toml` and use
`journalctl -u sip2nostr` instead of a log file; set `console_quiet =
true` and `console_timestamps = false` under `[logging]` there too, or
its own startup line and timestamps just double up on journald's.

### Voicemail recordings on a shared volume

If `[voicemail].recordings_dir` points outside `/var/lib/sip2nostr` - a
NAS mount shared with other services, say,
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
