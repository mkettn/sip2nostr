# Hub Architecture

sip2nostr splits "how a call reaches sip2nostr" from "what sip2nostr does
with it" into three pieces: a source that produces calls, a hub that
routes them, and a chain of sinks that decide what happens to each one.
This keeps the call-handling logic (ring over Nostr, fall back to
voicemail, play local test audio) independent of any particular
transport, so a second call source - a modem attached over D-Bus - reuses
the same sinks without reimplementing that logic (see
`docs/receiving-modem-calls.md`).

```
 ICallSource, ICallSource   produce Calls (SipCallSource, ModemCallSource)
      │  OnIncomingCall(Call)
      ▼
 CallHub                routes every Call through the same sink chain
      │
      ▼
 ICallSink, ICallSink, ...   each gets a turn until one returns true
   (NosCallSink, VoicemailSink, LocalTestAudioSink)
```

- **`ICallSource`** (`Hub/ICallSource.cs`) is anything that can produce an
  already-answered `Call`: `Sip/SipCallSource.cs`, which owns SIP
  registration, transport, and answering the INVITE, and
  `Modem/ModemCallSource.cs`, which talks to ModemManager over the system
  D-Bus to answer a call on a directly attached phone/modem device instead
  (see `docs/receiving-modem-calls.md`). Both attach to the same `CallHub`
  and need no changes anywhere else in the sink chain.
- **`Call`** (`Hub/Call.cs`) is the source-agnostic handle a sink works
  with: a call-id, the caller's number, the source's own line label (for
  per-line sink behavior, e.g. `LocalTestAudioSink`'s test sound), an
  `ICallAudio`, a way to hang up, and a task that completes when the call
  ends. There's no separate "answer" step at this level - a source only
  ever raises `OnIncomingCall` once audio is actually flowing, so a `Call`
  is always ready to bridge or record immediately.
- **`ICallAudio`** (`Hub/ICallAudio.cs`) has two send paths. `Send(RtpAudioFrame
  frame)` relays an RTP payload plus just enough header (timestamp, marker
  bit, payload type) to resend it unchanged elsewhere - the hub's fixed
  exchange format for relayed audio, see "Why RTP, not PCM" below.
  `SendEncodedSample(uint durationRtpUnits, byte[] sample)` is for a sink
  that generates audio instead of relaying it (a greeting, a tone): it
  matches SIPSorcery's own `AudioExtrasSource.OnAudioSourceEncodedSample`
  signature exactly, so that off-the-shelf player can be wired straight
  into it rather than a sink reimplementing RTP timestamp/pacing itself.
  `Sip/RtpSessionCallAudio.cs` adapts both to sipsorcery's `RTPSession`
  (used for both the SIP leg and, in `NosCallSink`, the WebRTC leg -
  `RTCPeerConnection` is itself an `RTPSession` subclass): `Send` relays
  `OnRtpPacketReceived` payloads unchanged via `SendRtpRaw` - no decode/
  encode - while `SendEncodedSample` is a straight passthrough to
  `RTPSession.SendAudio`. `Call.AudioFormat` carries the codec those
  frames are encoded with, for the sinks that need actual PCM samples
  (`VoicemailSink`, `LocalTestAudioSink`) or need to negotiate a matching
  format on another leg (`NosCallSink`).
- **`ICallSink`** (`Hub/ICallSink.cs`) is anything `CallHub` can offer a
  `Call` to. Returning `true` means it handled the call end to end
  (bridged it until hangup, or recorded a voicemail); `CallHub` won't try
  any further sinks. Returning `false` means it declined (e.g.
  `NosCallSink`'s ring timeout elapsed) and the next configured sink gets
  a turn.
- **`CallHub`** (`Hub/CallHub.cs`) ties a set of sources to one ordered
  sink chain. If no sink handles a call, it's left connected until the
  caller hangs up - which is also what happens with `[nostr].enabled =
  false` or `[voicemail].enabled = false` with nothing else configured,
  since neither condition puts a matching sink in the chain.

## Sink wiring

`Program.cs` builds the sink chain from config, once, at startup:

- `[nostr].enabled = true` → `NosCallSink`, then (if `[voicemail].enabled`)
  `VoicemailSink`. `NosCallSink` only applies a ring timeout when
  voicemail is enabled - otherwise it rings until the caller hangs up - so
  it takes that as a plain `int?` rather than a `VoicemailConfig`
  reference, meaning it doesn't need to know voicemail exists as a
  concept, only how long to wait before giving up.
- `[nostr].enabled = false` → `LocalTestAudioSink` only. This is a
  dev/testing path (see `docs/receiving-calls.md`), not a sink that's ever
  combined with the other two.

Each sink only receives the config it actually needs; none of them checks
`nostrConfig.Enabled` or `voicemailConfig.Enabled` itself, since whether a
sink is even in the chain already encodes that.

## Why RTP, not PCM, is the hub's exchange format

`ICallAudio` relays `RtpAudioFrame`s - RTP payload bytes plus enough
header (timestamp, marker bit, payload type) to resend unchanged - rather
than decoded PCM samples. The SIP leg and WebRTC leg are both RTP-shaped
underneath (SIP's `RTPSession`, WebRTC's `RTCPeerConnection`, itself an
`RTPSession` subclass), so relaying RTP frames directly keeps
`NosCallSink`'s SIP↔WebRTC bridge a zero-cost raw relay: both legs are
restricted to the same negotiated codec (`Call.AudioFormat`), so payloads
forward byte-for-byte with no decode/re-encode step.

A source or sink whose transport isn't RTP-shaped encodes/decodes at its
own boundary instead of the hub doing it for every pair regardless of
need. `Modem/ModemCallAudio.cs` is the clearest example: it's raw PCM off
an ALSA device (see `docs/receiving-modem-calls.md` for why), encoded
to/decoded from G.711 against `Call.AudioFormat` right there in the
adapter - `CallHub` and every sink still only ever see `RtpAudioFrame`s,
exactly as if the call had arrived over SIP. `VoicemailSink` and
`LocalTestAudioSink` work the same way on the sink side: recording
decodes each frame via
`SIPSorcery.Media.AudioEncoder.DecodeAudio` against `Call.AudioFormat`,
and playback (a greeting, a tone, a looped sound file) uses SIPSorcery's
own `AudioExtrasSource`, wired into `ICallAudio.SendEncodedSample`, so
that off-the-shelf player manages RTP timestamp/pacing rather than either
sink reimplementing it. Recoding is a source/sink concern, not something
the hub forces onto every pair.

## Forward-compatibility: outbound dialing

sip2nostr is designed to support different sources (SIP and a directly
attached modem/D-Bus line today) and different sinks (NosCall, voicemail) without
duplicating call-handling logic for each combination. A related goal
shaped some of the naming choices without being built yet:
**outbound dialing isn't implemented**, but nothing here should need to
be reshaped to add it later - e.g. NosCall gaining a "dial this number"
DM that makes the bridge originate a call to both `target_npub` and a
PSTN number.

Concretely, that's why:

- The call model is named `Call`, not `IncomingCall` - the record itself
  (call-id, caller info, `ICallAudio`, hangup, hangup-signal) doesn't
  assume a direction. An outbound call still needs the exact same shape:
  something to bridge audio through and a way to tear it down.
- `ICallSource.OnIncomingCall` is deliberately not the interface's only
  possible member. A source that can also originate calls would get a
  second, additive method (e.g. `DialAsync(string number, CancellationToken
  ct) -> Task<Call>`) - a source that only ever receives, like
  `SipCallSource` might stay for a while, doesn't have to implement it.
- The hub's mental model is "bridge two `ICallAudio` legs," not
  "answer an inbound SIP call" - that's already direction-agnostic, since
  `RtpSessionCallAudio` doesn't care whether the `RTPSession` it wraps was
  created by answering an INVITE or by sending one.

None of this is scaffolding sitting unused today - every piece above is
load-bearing for the inbound-only feature set that does exist. It's
mentioned here so the next person adding outbound dialing knows which
design decisions were made with it in mind, and doesn't need to guess
whether `Call`'s name was an oversight.
