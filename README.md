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

Design sketch / architecture skeleton. No working code yet — this README is
the reference for what gets implemented.

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

[dns]
# Address of the resolver to use for SIP hostname lookups.
# Falls back to system resolver if omitted.
resolver = "1.1.1.1:53"
resolver_fallback = "9.9.9.9:53"
timeout_ms = 2000

[[lines]]
uri = "sip:+4989123456@sip.your-provider.de"
label = "main"

[[lines]]
uri = "sip:+4989123457@sip.your-provider.de"
label = "fax"

[nostr]
relays = ["wss://relay.example.com", "wss://relay2.example.com"]
bridge_nsec = "nsec1..."      # this daemon's own identity, added as a contact in the receiving client
target_npub = "npub1..."      # your identity — every call latches here in the MVP (see below)

[webrtc]
stun_servers = ["stun:stun.l.google.com:19302"]
turn_server = ""               # optional, recommended for NAT traversal
```

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
generates an SDP offer, gift-wraps it (NIP-17/44/59) via Nostr.Sdk, and
publishes it to `target_npub` on the configured relays. Waits for the
answer + ICE candidates back over Nostr, feeds them into sipsorcery's
WebRTC session.

Exact event `kind`/tag layout must match whatever the receiving client
(e.g. NosCall) expects — there is no ratified NIP for call signaling yet,
so this is read directly out of the target client's source before
implementing.

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

- [ ] Confirm exact call-signaling event format expected by the target
      Nostr client (NosCall or other) — pull from its source.
- [ ] Confirm Opus codec support path in sipsorcery (built-in vs.
      supplementary package).
- [ ] Per-line routing (map individual `[[lines]]` entries to distinct
      `target_npub`s) — deferred past MVP.
- [ ] TURN server requirement — likely needed since the receiving client is
      usually behind NAT.
- [ ] Fallback behavior if the Nostr side doesn't answer within N seconds
      (e.g. voicemail, or ring a backup SIP extension).
- [ ] DoT/DoH support for the configurable resolver (currently plain DNS
      only in the initial design).
- [ ] Monitor Nostr.Sdk releases for breaking changes given its alpha status.

## License

TBD.
