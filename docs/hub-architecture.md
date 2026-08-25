# Hub Architecture

sip2nostr originally had one call path hard-wired end to end: `BridgeService`
answered SIP INVITEs and handed them straight to `CallBridge`, which decided
whether to ring over Nostr, fall back to voicemail, or (with `[nostr].enabled
= false`) just play local test audio - all in one class, one method. That
worked for a single SIP trunk with exactly two possible outcomes, but it
doesn't generalize: a future call source (a modem attached over D-Bus,
say) would have needed its own copy of the same ring/voicemail/test-audio
decision tree, because nothing about that decision tree was separable from
`CallBridge`'s SIP-specific types.

This refactor splits "how a call reaches sip2nostr" from "what sip2nostr
does with it" into three pieces:

```
 ICallSource            produces Calls (SipCallSource today)
      │  OnIncomingCall(Call)
      ▼
 CallHub                routes every Call through the same sink chain
      │
      ▼
 ICallSink, ICallSink, ...   each gets a turn until one returns true
   (NosCallSink, VoicemailSink, LocalTestAudioSink)
```

- **`ICallSource`** (`Hub/ICallSource.cs`) is anything that can produce an
  already-answered `Call` - today, `Sip/SipCallSource.cs`, which owns SIP
  registration, transport, and answering the INVITE. A future source (a
  modem's ALSA/D-Bus line, say) implements the same interface and needs no
  changes anywhere else.
- **`Call`** (`Hub/Call.cs`) is the source-agnostic handle a sink works
  with: a call-id, the caller's number, the source's own line label (for
  per-line sink behavior, e.g. `LocalTestAudioSink`'s test sound), an
  `ICallAudio`, a way to hang up, and a task that completes when the call
  ends. There's no separate "answer" step at this level - a source only
  ever raises `OnIncomingCall` once audio is actually flowing, so a `Call`
  is always ready to bridge or record immediately.
- **`ICallAudio`** (`Hub/ICallAudio.cs`) is bidirectional 8 kHz mono
  16-bit PCM, regardless of the transport underneath. `Sip/
  RtpSessionCallAudio.cs` adapts it to sipsorcery's `RTPSession` (used for
  both the SIP leg and, in `NosCallSink`, the WebRTC leg - `RTCPeerConnection`
  is itself an `RTPSession` subclass). Decoding SIP audio to PCM and
  re-encoding it for WebRTC (rather than the pre-refactor raw-RTP relay,
  which only worked because both legs happened to negotiate the same G.711
  payload) is the price of this abstraction - see "Why PCM" below.
- **`ICallSink`** (`Hub/ICallSink.cs`) is anything `CallHub` can offer a
  `Call` to. Returning `true` means it handled the call end to end
  (bridged it until hangup, or recorded a voicemail); `CallHub` won't try
  any further sinks. Returning `false` means it declined (e.g.
  `NosCallSink`'s ring timeout elapsed) and the next configured sink gets
  a turn.
- **`CallHub`** (`Hub/CallHub.cs`) ties a set of sources to one ordered
  sink chain. If no sink handles a call, it's left connected until the
  caller hangs up - unchanged from the pre-refactor behavior for
  `[nostr].enabled = false` or `[voicemail].enabled = false` with nothing
  else configured.

## Sink wiring

`Program.cs` builds the sink chain from config, once, at startup:

- `[nostr].enabled = true` → `NosCallSink`, then (if `[voicemail].enabled`)
  `VoicemailSink`. `NosCallSink` only applies a ring timeout when
  voicemail is enabled (otherwise it rings indefinitely, matching
  pre-refactor behavior) - it takes that as a plain `int?` rather than a
  `VoicemailConfig` reference, so it doesn't need to know voicemail exists
  as a concept, only how long to wait before giving up.
- `[nostr].enabled = false` → `LocalTestAudioSink` only. This is a
  dev/testing path (see `docs/receiving-calls.md`), not a sink that's ever
  combined with the other two.

Each sink only receives the config it actually needs; none of them checks
`nostrConfig.Enabled` or `voicemailConfig.Enabled` itself, since whether a
sink is even in the chain already encodes that.

## Why PCM instead of a raw RTP relay

The pre-refactor `CallBridge.BridgeAudio` forwarded RTP packets unchanged
between the SIP and WebRTC `RTPSession`s - zero-cost, but only correct
because both sides were forced onto the same G.711 payload type. Bridging
through `ICallAudio` instead means every sink (and every future source)
speaks the same 8 kHz mono PCM regardless of what codec its transport
negotiated, at the cost of a decode/re-encode step on the path that used
to be a raw relay (`NosCallSink`). That trade was made deliberately: a
raw-RTP contract can't survive a source or sink whose transport doesn't
happen to speak the same RTP payload type (WebRTC choosing Opus, a modem's
ALSA device, anything that isn't G.711 SIP-to-SIP), and this refactor
exists specifically to stop assuming there's only ever one of each.

## Forward-compatibility: outbound dialing

The immediate motivation for this refactor was wanting different sources
(SIP today, maybe a modem/D-Bus line later) and different sinks (NosCall,
voicemail) without duplicating the call-handling logic for each
combination. A second, related goal shaped some of the naming choices
without being built now: **outbound dialing isn't implemented**, but
nothing here should need to be reshaped to add it later - e.g. NosCall
gaining a "dial this number" DM that makes the bridge originate a call to
both `target_npub` and a PSTN number.

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

## What changed for existing features

Behavior is unchanged from the caller's and `target_npub`'s perspective;
see `docs/receiving-calls.md` and `docs/voicemail.md` for the updated
per-feature flow descriptions and implementation notes. In short:

- `BridgeService` → `Sip/SipCallSource.cs` (SIP transport/registration/
  trace-logging carried over essentially unchanged; only how it hands off
  an answered call is new).
- `CallBridge`'s Nostr/WebRTC bridging → `Sinks/NosCallSink.cs`.
- `CallBridge`'s voicemail recording → `Sinks/VoicemailSink.cs`.
- `CallBridge`'s `[nostr].enabled = false` local-audio path →
  `Sinks/LocalTestAudioSink.cs`, now built on the same `RTPSession` +
  `RtpSessionCallAudio` path as everything else rather than a separate
  `AudioSendOnlyMediaSession`.
- Sound-file resolution (`[[lines]].sound`, `[voicemail].greeting_sound`) →
  `Shared/SoundFileResolver.cs`, shared by `LocalTestAudioSink` and
  `VoicemailSink` instead of living inside `CallBridge`.
