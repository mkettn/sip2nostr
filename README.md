# sip2nostr

A small gateway that answers incoming calls on an existing VoIP landline
SIP account and forwards them as a call-signaling request over Nostr to a
compatible client (e.g. NosCall), bridging the audio between the SIP/RTP leg
and a WebRTC leg.

This replaces a softphone (e.g. Twinkle) as the thing registered to your VoIP
provider. Instead of ringing a phone, an inbound call rings your Nostr
identity on any device running a call-capable Nostr client.

No UI — configuration is a single TOML file, no more.

## Status

Verified against a real SIP trunk: with `[nostr].enabled = false`, sip2nostr
registers, answers an inbound call, plays a local test audio file, and
tears the call down cleanly on `BYE` (see `docs/receiving-calls.md` for the
full debugging trace and root cause). With `[nostr].enabled = true`,
propagation to a real NosCall install is verified end-to-end over NIP-AC:
NosCall rings, answers, and audio flows both ways — see
`docs/propagating-to-nostr.md` for the protocol and its blind spots. Note
NosCall only accepts calls from a followed contact, so the bridge's pubkey
(from `bridge_nsec`) needs to be added as a contact there first - printed
as `npub1...` on every startup so there's no need to derive it by hand.
The `[voicemail]` answer-timeout fallback is also verified end-to-end
against a real SIP trunk: the greeting/tone plays, the caller's audio is
recorded, encoded to Opus/OGG, and delivered as a NIP-17 DM that a
receiving client can decrypt and play back — see `docs/voicemail.md`.

Copy `config.example.toml` to `config.toml`, fill in your SIP and Nostr
credentials, and run:

```
cd src/Sip2Nostr
dotnet run -- ../../config.toml
```

## Downloading a release

Tagged releases (`vX.Y.Z`) are built automatically for Linux x86_64 and
arm64 (Raspberry Pi 4/5 on the 64-bit OS) via GitHub Actions — see the
[Releases](../../releases) page. Each release has four assets:

| Asset suffix | Use when... |
|---|---|
| `linux-x64-selfcontained.tar.gz` | Deploying to an amd64 server/VM with no .NET runtime installed. |
| `linux-x64-framework.tar.gz` | The amd64 target already has the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) installed; smaller download. |
| `linux-arm64-selfcontained.tar.gz` | Deploying to a Raspberry Pi (arm64) with no .NET runtime installed — the simplest option for a fresh Pi. |
| `linux-arm64-framework.tar.gz` | The Pi already has the .NET 8 runtime installed; smaller download. |

Each tarball also includes `config.example.toml` and `README.md`. Extract
it, copy `config.example.toml` to `config.toml`, fill in your credentials,
and run the `Sip2Nostr` binary directly (self-contained) or via
`dotnet Sip2Nostr.dll` (framework-dependent).

## Architecture: single binary, C#/.NET

```
 VoIP provider (SIP trunk, possibly multiple lines)
        │  REGISTER / INVITE / RTP (G.711)
        ▼
 ┌───────────────────────────────────────┐
 │              sip2nostr                │
 │                                        │
 │  sipsorcery: SIP UA/RTP + WebRTC leg   │
 │  Nostr.Sdk (rust-nostr binding):        │
 │    call signaling over Nostr           │
 │  DnsClient.NET: configurable DNS       │
 │  Tomlyn: config.toml                   │
 └───────────────────┬───────────────────┘
                      ▼
           Nostr relay(s) (wss://)
                      │
                      ▼
           NosCall (or compatible client)
```

One language, one binary, no IPC boundary. This supersedes an earlier
two-binary (C++/PJSIP + Rust) design: **sipsorcery** is a pure C# library
that covers both the SIP/RTP leg and the WebRTC leg in one place, which is
what previously required two separate mature libraries in two different
languages. Combined with **Nostr.Sdk** — the official C# binding for
rust-nostr — a single C#/.NET process now covers every concern without
needing to split languages.

| Concern              | Library                          | Notes |
|-----------------------|-----------------------------------|-------|
| SIP / RTP + WebRTC    | **sipsorcery**                    | Pure C#, actively maintained (~1.9k stars, commits as recent as mid-2026). Covers SIP registration/INVITE/RTP and WebRTC/ICE/DTLS-SRTP in one library — no audio device capture needed here since audio is bridged programmatically, not played to a soundcard. Opus codec support may need a supplementary package; confirm at implementation time. |
| Nostr protocol        | **Nostr.Sdk**                     | Official rust-nostr binding (UniFFI-generated, same project as the Rust/Swift/Kotlin bindings, not third-party). NIP-17/NIP-44/NIP-59 support inherited from the core Rust crate. Marked **ALPHA** upstream — expect breaking API changes between versions. |
| DNS resolution        | **DnsClient.NET**                 | Mature .NET resolver library with explicit, configurable nameserver support (required feature, see below). |
| Config                | **Tomlyn**                        | TOML parser for .NET. |

Tradeoff worth naming: sipsorcery is newer and less battle-tested at telecom
scale than PJSIP (which has ~20 years of production deployment behind it),
and Nostr.Sdk's alpha status carries some API-churn risk. For a single-line
personal MVP, both are a reasonable bet; revisit if either becomes a
blocker once building.

Internally, call handling follows a hub/source/sink pattern: a `CallHub`
routes every call from an `ICallSource` (today, `SipCallSource`) through a
configured chain of `ICallSink`s (`NosCallSink`, `VoicemailSink`,
`LocalTestAudioSink`) — see `docs/hub-architecture.md` for why, and for how
this keeps the door open to future sources (e.g. a modem/D-Bus line) and
sinks without reshaping the core interfaces.

## Required feature: configurable DNS resolver

The hostname resolution used to reach the VoIP provider's SIP registrar/proxy
**must not** be hardcoded to the OS-configured resolver. It must be
configurable per deployment (custom DNS server, custom port), independent of
whatever the host machine uses system-wide — implemented via a
`DnsClient.NET` `LookupClient` configured from `[dns]` in `config.toml` and
used explicitly for the SIP transport's hostname resolution, rather than
relying on `System.Net.Dns`/the OS resolver.

## Configuration (`config.toml`)

```toml
[sip]
provider_host = "sip.your-provider.de"
username = "YOUR_SIP_USER"
password = "YOUR_SIP_PASS"
# Optional: set this to the public host/IP your SIP provider should use
# for inbound calls if REGISTER succeeds but no INVITE reaches this process.
# contact_host = "203.0.113.10"
# Local RTP port for SIP audio.
rtp_port = 8000

[dns]
# Address of the resolver to use for SIP hostname lookups.
# Falls back to system resolver if omitted.
resolver = "1.1.1.1:53"
resolver_fallback = "9.9.9.9:53"
timeout_ms = 2000

[logging]
# Optional: write each process run to its own log file.
# Relative paths are resolved next to this config file. If the path does
# not include {timestamp} or {run}, a timestamp is added before the extension.
run_file = "logs/sip2nostr-{timestamp}.log"

[[lines]]
uri = "sip:+4989123456@sip.your-provider.de"
label = "main"
# Optional: when [nostr].enabled is false, answer calls on this line and
# play this file on loop to test SIP audio. Raw 8 kHz 16-bit PCM works
# directly; mono Ogg/Opus (.ogg/.opus) is decoded in-process - see
# docs/sound-files.md for exactly what's supported and how to convert a file.
# sound = "sounds/test.opus"

[[lines]]
uri = "sip:+4989123457@sip.your-provider.de"
label = "fax"

[nostr]
enabled = true
relays = ["wss://relay.example.com", "wss://relay2.example.com"]
bridge_nsec = "nsec1..."      # this daemon's own identity, added as a contact in the receiving client
target_npub = "npub1..."      # your identity — every call latches here in the MVP (see below)

[webrtc]
stun_servers = ["stun:stun.l.google.com:19302"]
turn_server = ""               # optional, recommended for NAT traversal

[voicemail]
enabled = false                # opt-in: falls back to a greeting + recording if target_npub doesn't answer
ring_timeout_seconds = 20
max_recording_seconds = 60
# greeting_sound = "sounds/greeting.opus"   # optional; a short tone plays if unset
# dm_relays = ["wss://dm-relay.example.com"] # optional; defaults to [nostr].relays
delivery = "audio"             # or "text" - see [voicemail.transcription] below

[voicemail.transcription]      # only consulted when delivery = "text"
engine = "whisper"
# model_path = "models/ggml-base.en.bin"   # required for delivery = "text"
# language = "en"                          # optional; auto-detected if unset
```

## Voicemail: answering-machine fallback

Opt-in (`[voicemail].enabled = false` by default). When enabled, if
`target_npub` doesn't answer a call over Nostr within
`[voicemail].ring_timeout_seconds`, the call diverts to a local greeting
(or a short tone if `greeting_sound` isn't configured) followed by a
recording of up to `max_recording_seconds`, encoded and saved locally as
Ogg/Opus (in-process via `Concentus` — pure C#, no external program
required) under `[voicemail].recordings_dir`, named per
`[voicemail].recording_filename` (a template with `{timestamp}`,
`{caller}`, and `{call_id}` placeholders — defaults to
`{timestamp}-{caller}.ogg`), and the SIP call hung up immediately —
delivery happens off the call's critical path, handed to a
background worker (`Voicemail/VoicemailSender.cs`, one instance shared
for the process lifetime) that connects to `[voicemail].dm_relays` (or
`[nostr].relays` as a fallback — a NIP-17 DM inbox, kind:10050, can
legitimately differ from the relays used for call signaling) only when
something's queued, sends it as a Nostr direct message, then disconnects.
How the recording turns into DM content is pluggable via
`[voicemail].delivery`: `"audio"` (default) inlines the already-encoded
Ogg/Opus recording directly, so `max_recording_seconds` is capped by
what reliably fits a NIP-17 DM (27s by default); `"text"` transcribes it
offline via Whisper.net and sends the transcript instead, no relay-side
or third-party involvement needed for the transcription itself (just a
local GGML model file), so it's instead capped by the separately
configurable `[voicemail].max_text_recording_seconds` (default 600s, a
memory-use sanity limit rather than a DM size budget). Exactly one DM
per missed call: the recording, or - if the caller hung up before
anything worth sending was captured - a plain-text missed-call notice
naming the caller. Recordings on disk don't depend on delivery
succeeding. See `docs/voicemail.md` for the full flow and known
limitations — notably, the recording is inlined directly into the DM
rather than uploaded to a file host, which is what caps
`max_recording_seconds`'s default well below a minute: NIP-17's own
encryption (not just a relay's size limit) can't carry much more than
~27 seconds of audio at the current encoding.

## Multiple lines, single identity (MVP)

A VoIP gateway/account may expose multiple lines (multiple registered SIP
URIs/DIDs), hence `[[lines]]` above. In the MVP, **every inbound call on
every configured line latches to the same single `target_npub`** — there is
no per-line routing yet. The config still lists lines explicitly so the
shape is in place for later per-line routing without a breaking config
change; the line label is available internally when a call comes in, it's
just not used for routing decisions yet.

## Components

### 1. SIP/RTP + WebRTC (sipsorcery)
Registers to the VoIP provider (one or more lines), answers inbound
INVITEs, and owns both the SIP/RTP leg and the WebRTC leg — the same
library handles the audio path on both sides, so bridging is in-process
rather than across a socket or FFI boundary.

### 2. Nostr signaling (Nostr.Sdk)
On an inbound call, opens a WebRTC peer connection via sipsorcery,
generates an SDP offer, wraps it per NIP-AC (NIP-44, ephemeral per-message
keypair, no seal layer) via Nostr.Sdk, and publishes it to `target_npub` on
the configured relays. Waits for the answer + ICE candidates back over
Nostr, feeds them into sipsorcery's WebRTC session. See
`docs/propagating-to-nostr.md` for the protocol, sourced directly from
NosCall's own implementation.

### 3. DNS resolution (DnsClient.NET)
Wraps a configurable `LookupClient` from `[dns]` in `config.toml`, used for
the SIP transport's hostname resolution instead of relying on the OS
resolver. Falls back to the system resolver only if no `[dns]` section is
present.

### 4. Config (Tomlyn)
Parses `config.toml` (SIP credentials, lines, DNS resolver, Nostr
keys/relays, WebRTC STUN/TURN) at startup. No runtime UI or admin surface.

## Dependencies (planned)

- sipsorcery
- Nostr.Sdk
- DnsClient.NET
- Tomlyn

## Open questions / TODO

- [x] Confirm exact call-signaling event format expected by the target
      Nostr client (NosCall) — pulled from its source (NIP-AC), verified
      end-to-end against a real install. See `docs/propagating-to-nostr.md`.
- [x] Codec: implemented using G.711 (PCMU/PCMA) on both the SIP and WebRTC
      legs, no transcoding — sipsorcery supports this out of the box via
      `MediaStreamTrack(SDPWellKnownMediaFormatsEnum[])`, no supplementary
      Opus package needed. Revisit if NosCall doesn't offer PCMU/PCMA.
- [x] Per-line routing: not needed for MVP, confirmed — every line still
      latches to the single configured `target_npub`.
- [ ] TURN server requirement — verified working over a local network with
      STUN only; TURN/NAT behavior across the open internet is still
      untested (see `docs/propagating-to-nostr.md` blind spots).
- [x] Fallback behavior: implemented, opt-in (`[voicemail].enabled = false`
      by default), verified end-to-end against a real SIP trunk — if
      enabled and the Nostr side doesn't answer within
      `[voicemail].ring_timeout_seconds`, the call falls back to a local
      greeting + recording, sent to `target_npub` as a Nostr DM. See
      `docs/voicemail.md`.
- [ ] DoT/DoH support for the configurable resolver (currently plain DNS
      only in the initial design).
- [ ] Monitor Nostr.Sdk releases for breaking changes given its alpha status.

## License

TBD.
