# Receiving SIP Calls

This documents how sip2nostr answers an inbound call today, verified
against a real SIP trunk with `[nostr].enabled = false` (the local
test-audio path). It also covers the parts of the flow that carry over
unchanged once Nostr/WebRTC propagation is finished, and lists the known
blind spots and loose ends in the current implementation.

Design choices here were sanity-checked against a working Twinkle
softphone trace on the same trunk — Twinkle is not a dependency or a
runtime component, just a known-good reference for what a compliant
response looks like on this provider.

## Overview

Since the hub-architecture refactor (see `docs/hub-architecture.md`),
owning the SIP leg and deciding what to do with a call are two separate
concerns. `SipCallSource` (an `ICallSource`) registers once for the whole
SIP trunk (`[sip]` in `config.toml`), keeps a single `SIPUserAgent`
listening for inbound `INVITE` requests on TLS port 5061 (`[sip].tls`,
the default) or UDP port 5060 (`[sip].tls = false`), and accepts every
call itself — regardless of which configured `[[lines]]` DID it targets.
The real-trunk verification below predates `[sip].tls` and ran over
plain UDP - see `docs/propagating-to-nostr.md`'s Blind spots for the
TLS path's own (more limited) verification. It raises `OnIncomingCall` with a transport-agnostic `Call` while
the caller is still ringing, and `CallHub` routes that through the
configured `ICallSink` chain (`NosCallSink`, `VoicemailSink`,
`LocalTestAudioSink`). The `200 OK` goes out only when a sink calls
`Call.AnswerAsync` — see "Who answers, and when" in
`docs/hub-architecture.md` — so the caller hears normal ringback until
something is actually ready to take the call.

```
 VoIP provider
      │  REGISTER (once, at startup)
      │  INVITE (per call)
      ▼
 SipCallSource               — registration, SIP transport, DNS/URI resolution,
      │                         accepting the SIP leg, answering it on request
      │  OnIncomingCall(Call)  — ringing, not yet answered
      ▼
 CallHub                     — routes the Call through the sink chain
      │
      ├─ [nostr].enabled = true  → NosCallSink, then VoicemailSink if
      │    configured and the ring times out
      │
      └─ [nostr].enabled = false → LocalTestAudioSink
           (verified: this doc)
```

## Step by step: taking a call

1. **Registration.** At startup, `SipCallSource.StartAsync` resolves the
   provider host through the configurable DNS resolver (see below),
   builds one `SIPRegistrationUserAgent` for the whole account, and starts
   it. A single successful `REGISTER` covers every configured line; the
   `[[lines]]` entries are DIDs that can ring on that one registration, not
   separate accounts.

2. **Inbound `INVITE` dispatch.** `SIPUserAgent.OnIncomingCall` fires for
   every inbound `INVITE`. `SipCallSource.HandleIncomingCall` matches the
   `INVITE`'s Request-URI user part against the configured `[[lines]]`
   entries — this becomes `Call.LineLabel`, which `LocalTestAudioSink` uses
   to pick a per-line sound; there is still no per-line routing decision for
   the Nostr path (see Blind Spots). The call is then handled by
   `SipCallSource.AcceptAndRouteCallAsync`.

3. **Codec selection.** `SelectOfferedG711Format` scans the offered SDP's
   `m=audio` payload list for RTP payload type `8` (PCMA) or `0` (PCMU) and
   picks whichever the provider offered; PCMA wins if both are absent from
   the offer (an unlikely case for a PSTN-facing trunk). No transcoding is
   ever needed on the SIP leg — G.711 is used because it's what every PSTN
   gateway offers, and sipsorcery supports it directly via
   `SDPWellKnownMediaFormatsEnum`, no supplementary codec package required.

4. **Accepting, ringing, and answering.** `ua.AcceptCall(inviteRequest)`
   creates the `SIPServerUserAgent` for the transaction and sends
   `100 Trying` and `180 Ringing` itself, which is what makes the caller's
   own phone/provider generate ringback. The `INVITE` transaction is then
   left open: a plain `RTPSession` carrying just the selected G.711 format
   is built up front, wrapped in `RtpSessionCallAudio` (an `ICallAudio`
   adapter that relays inbound/outbound RTP audio payloads unchanged — no
   decode/encode) and handed to `CallHub` as part of a `Call`, which also
   carries the negotiated `AudioFormat` so a sink can decode/negotiate
   against it if it needs to. Every call is then answered the same way
   regardless of what happens next, but only when a sink calls
   `Call.AnswerAsync`: via `SIPUserAgent.Answer(uas, mediaSession,
   customHeaders: null, publicIpAddress: localMediaAddress)` —
   sipsorcery's own supported answer path, not a hand-built response.
   Answering happens at most once per call, and a call nothing ever
   answered has its media session closed and its `INVITE` transaction
   turned down rather than being left pending. When and why each sink
   answers is a sink's job, not the source's:

   - **Local test audio (`LocalTestAudioSink`, verified):** answers
     immediately, then plays either a
     configured sound file (`[[lines]].sound`, decoded to raw 8 kHz PCM
     in-process via `Sip/OpusCodec.cs` if it isn't already
     `.pcm`/`.raw`/`.s16le` - see `docs/sound-files.md` for what's
     actually supported) or a sine wave test tone on loop over
     `Call.Audio` until the caller hangs up, via SIPSorcery's own
     `AudioExtrasSource` wired into `Call.Audio.SendEncodedSample`. Only
     wired in when `[nostr].enabled = false`.
   - **Nostr/WebRTC bridging (`NosCallSink`, implemented and verified end-
     to-end):** rings for as long as the Nostr side does — it answers the
     SIP leg only once a real SDP answer comes back. It creates an
     `RTCPeerConnection` for the WebRTC leg,
     restricted to the same negotiated `Call.AudioFormat` as the SIP leg,
     wrapped in its own `RtpSessionCallAudio`, and bridges the two
     `ICallAudio` legs by forwarding RTP frames unchanged each way — a raw
     relay, same as the pre-hub implementation, since both legs are forced
     onto the same codec. A `NostrSignalingClient` connects, an SDP offer
     is generated from the `RTCPeerConnection` and gift-wrapped to
     `target_npub`, and the call waits for a gift-wrapped SDP answer and
     ICE candidates back before completing the WebRTC side. See
     `docs/propagating-to-nostr.md` for the protocol.

5. **Response header shaping.** `SipCallSource.InstallSipTraceLogging`
   installs `SIPTransport.CustomiseRequestHeader` /
   `CustomiseResponseHeader` hooks that run for every outbound SIP message.
   For `INVITE` responses at `180` and above, these hooks force the
   `Contact` header to `sip:<account-number>@<contact-host>` (the
   configured `[sip].contact_host`, or the local send interface address if
   unset), and for the final `200 OK` also set `Allow`, `Supported`, and
   `Content-Length` explicitly rather than trusting sipsorcery's defaults.

6. **Call teardown.** Every sink races its own work against
   `Call.WhenRemoteHungUp`, and `CallHub` calls `Call.HangupAsync`
   unconditionally once a sink is done. `WhenRemoteHungUp` completes on
   any of the four ways a call can end from the source's side: a `BYE`
   from the provider on an answered call (`ua.OnCallHungup`), a `CANCEL`
   while it's still ringing (`SIPServerUserAgent.CallCancelled` — the
   dialogue-level hangup event never fires for a call that was never
   answered), sipsorcery expiring an `INVITE` left ringing for
   `SIPTimings.MAX_RING_TIME`/3 minutes (`NoRingTimeout`), or the
   passed-in shutdown `CancellationToken`. A sink can also end a call
   itself by returning — `NosCallSink` does exactly that when the Nostr
   side hangs up (see `docs/propagating-to-nostr.md`). `Call.HangupAsync`
   sends `BYE` for an answered call, and turns down one that was never
   answered on its pending `INVITE` with `480 Temporarily Unavailable` —
   skipped for a call the caller already cancelled, or one whose
   transaction expired, since neither can take another final response. A
   local shutdown still closes the session without sending `BYE` itself
   (see Blind Spots).

## Why response routing needs the configurable DNS resolver to actually work

The README requires a configurable DNS resolver instead of the OS
resolver, for reaching the provider's registrar. That same resolver
machinery also has to handle a case that's easy to overlook: **every
response sip2nostr sends back to a caller is addressed using the literal
IP address taken from the request's `Via` header** — never a hostname.
sipsorcery's own default resolver (`SIPDns`) special-cases literal IP
addresses and resolves them synchronously with no lookup at all; a custom
resolver that only special-cases the configured provider *hostname* and
returns nothing for anything else will cause every outbound response
(`100 Trying`, `180 Ringing`, `200 OK`, and their retransmits) to silently
never be sent — the call rings and answers locally, but the caller never
hears anything, with no error logged anywhere, because sipsorcery's
retransmit-timer logging fires on schedule regardless of whether the
underlying send actually succeeded.

`SipCallSource.InstallSipUriResolver` handles this by resolving literal IP
addresses immediately in both the synchronous cache callback
(`ResolveSIPUriFromCacheCallback`) and the async fallback
(`ResolveSIPUriCallbackAsync`), before falling back to the configured DNS
resolver for the one real hostname lookup this process ever needs — the
registrar.

## Blind spots and loose ends

- **DTMF is not relayed**, not just unimplemented: `SipCallSource` always
  builds the SIP audio track from a single negotiated G.711 format
  (`CreateAudioTrack`), which carries no DTMF payload type. A caller
  pressing keys on their phone during a call answered by sip2nostr won't
  have those keypresses relayed anywhere. This wasn't a deliberate product
  decision, just not built yet.
- **The Nostr/WebRTC propagation path is implemented and verified
  end-to-end** against a real NosCall install over NIP-AC — see
  `docs/propagating-to-nostr.md` for the protocol and its own blind spots
  (TURN/NAT coverage, staleness/dedup, etc.).
- **No per-line routing.** Every configured `[[lines]]` DID rings the same
  `target_npub` once Nostr signaling is enabled; the matched line is
  currently used for logging only.
- **Answer-timeout fallback: implemented, opt-in, not yet verified
  end-to-end.** `[voicemail].enabled` is `false` by default. If turned on
  and the Nostr side never answers within `ring_timeout_seconds` (default
  20s), the call is diverted to a local greeting + recording instead of
  ringing indefinitely, and the recording is sent to
  `target_npub` as a Nostr DM. See `docs/voicemail.md` for the flow and
  its own blind spots (notably: no file-hosting upload path, so large
  recordings can exceed a relay's max event size).
- **Ringback until answer is implemented but not verified against a real
  trunk.** The caller now hears ringing until a sink answers, but what a
  provider does with an `INVITE` left ringing for a long time hasn't been
  observed: whether it applies its own ring timeout and `CANCEL`s first,
  and whether that lands before sipsorcery's `MAX_RING_TIME` (3 minutes)
  expires the transaction. Only matters when nothing picks up for minutes
  — with `[voicemail]` enabled, `ring_timeout_seconds` (default 20s) ends
  the ringing long before either.
- **Concurrent calls are untested.** Only one inbound call has been
  exercised at a time. Whether two simultaneous calls' `RTPSession`s
  collide, and whether the WebRTC path's per-call
  `RTCPeerConnection`/`RTPSession` pair is safely reentrant, has not been
  checked.
- **Registration renewal over a long run is unobserved.** Testing so far
  covers a single registration cycle within its `expiry` window; renewal
  behavior as the registration approaches expiry (and any retry behavior
  on a missed renewal) hasn't been watched over a multi-hour run.
- **No `BYE` on local shutdown.** If the process is stopped while a call is
  active, the media session is closed locally but no `BYE` is sent to the
  provider — the far end has to notice the RTP stream stop and/or time the
  dialog out on its own.
- **The `Contact: 0.0.0.0:0` placeholder fix depends on internal
  sipsorcery behavior** (`SIPTransport.AdjustHeadersForEndPoint` rewriting
  any header field that starts with `0.0.0.0`/`::0` to the real local
  send-from socket) that isn't part of sipsorcery's public API contract —
  it happens to work on the installed version but isn't something this
  codebase asserts or tests directly.
- **`Server`/`User-Agent` headers currently claim to be Twinkle**
  (`Twinkle/1.10.2`), left over from early debugging that turned out to be
  unrelated to the actual bug (see git history). This works, but
  identifying as another product isn't something to rely on long-term and
  is a reasonable thing to revisit — either drop it or replace it with an
  honest identifier.
- **IPv6 is untested.** The SIP transport binds to `IPAddress.Any` (IPv4
  wildcard) and all verified testing has been over IPv4.
- **TURN/NAT behavior for the WebRTC leg is untested beyond the local
  network the `docs/propagating-to-nostr.md` verification ran on.** The
  README notes TURN as "likely needed" but this hasn't been confirmed
  either way.
