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

Installs the binary, the systemd service unit, and the `sysusers.d`
file that creates the `sip2nostr` system user and its data directory
(`/var/local/lib/sip2nostr`, mode `0700`, owned by that user).
sip2nostr isn't a distro package, so everything fixed lives under
`/usr/local/lib` - not just the binary (`/usr/local/lib/sip2nostr/`),
but the `sysusers.d` file (`/usr/local/lib/sysusers.d/sip2nostr.conf`)
and the unit itself (`/usr/local/lib/systemd/system/sip2nostr.service`)
too, each in that directory tree's own canonical location for locally
installed software rather than `/etc` or `/usr/lib`. Everything
variable lives under `/var/local/lib` instead. Nothing is put on
`$PATH` - sip2nostr is meant to run under systemd, not invoked
directly by name. If `config.toml` doesn't already exist in the data
directory, install.sh also seeds it there from `config.example.toml` -
see "Running as a systemd service" below to finish setup and start it.

To upgrade an existing install: `./build.sh && sudo ./install.sh`, then
`sudo systemctl restart sip2nostr` - install.sh replaces the files but
never restarts an already-running service.

## Configuring

Copy `config.example.toml` to a `config.toml` of your choosing and fill
in your SIP and Nostr credentials - every option is documented inline
there. See `docs/` for the caller allow/deny-list, voicemail fallback,
and sound-file format in more detail.

## Running

```
/usr/local/lib/sip2nostr/Sip2Nostr /path/to/config.toml
```

Or, from source:

```
cd src/Sip2Nostr
dotnet run -- /path/to/config.toml
```

For a long-running deployment, use systemd instead - see below.

## Running as a systemd service

`sudo ./install.sh` (see "Installing" above) already creates the
`sip2nostr` user, its data directory, the service unit, and -
if `config.toml` doesn't already exist there - seeds one from
`config.example.toml`; a previous install's config always survives a
re-run either way. All that's left is filling in your SIP and Nostr
credentials and starting the service:

```
sudo nano /var/local/lib/sip2nostr/config.toml
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
