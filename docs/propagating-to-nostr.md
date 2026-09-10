# Propagating a Call to Nostr (NIP-AC)

This documents how sip2nostr turns an inbound SIP call into a ring on
NosCall, when `[nostr].enabled = true`, and carries the call through to
audio. Verified end-to-end against a real NosCall install (0.5.2) over a
local relay: NosCall rings, answers, and audio flows both ways.

**Requirement:** NosCall only accepts NIP-AC call events from a pubkey it
already follows (a NIP-02 contact-list check). Add the bridge's public key
(derived from `[nostr].bridge_nsec`) as a followed contact in NosCall
before testing, or every signaling event will be silently dropped as
`not-followed`. `Program.cs` prints this as `npub1...` on every startup
(`Bridge Nostr identity: ...`), so there's no need to derive it by hand.

## Where the protocol came from

The wire format below is **NIP-AC ("WebRTC Calls")**, a draft Nostr NIP,
cross-checked against NosCall's actual Dart implementation
(`lib/core/call/nip_ac_protocol.dart` and `lib/call/calling_controller.dart`
from `sanah9/noscall`). This is sourced, not guessed — earlier attempts in
this project used a standard NIP-59 gift wrap with made-up event kinds,
which was never going to interoperate with a real NosCall install.

## The protocol

**Event kinds:** `offer = 25050`, `answer = 25051`, `candidate = 25052`,
`hangup = 25053`, `reject = 25054`. (NIP-AC also defines `renegotiate =
25055` for mid-call SDP changes; NosCall's shipped protocol code doesn't
use it, and it's out of scope here regardless — it's a mid-call concern.)

**Inner signaling event** — signed with the bridge's own real identity
(`bridge_nsec`), not an ephemeral key:
- `tags`: `p` (recipient pubkey), `call-id` (a UUID v4, stable across every
  message for one call), `alt` (fixed string `"NIP-AC signaling"`, matching
  NosCall's own constant rather than the NIP doc's example text), and —
  **required, offer only** — `call-type` (`"voice"` here; sip2nostr never
  sends video). NosCall's decoder throws on an offer missing `call-type`.
- `content`: the raw SDP string for offer/answer (no JSON wrapper); JSON
  `{"candidate":..., "sdpMid":..., "sdpMLineIndex":...}` for a candidate; a
  plain reason string (or empty) for hangup/reject.

**Wrapping** — an ephemeral variant of NIP-59, *not* a standard gift wrap:
1. JSON-encode the already-signed inner event.
2. Generate a fresh ephemeral keypair for this one message.
3. NIP-44-encrypt the JSON as the ephemeral key, to the recipient.
4. Publish an outer event: `kind = 21059`, `pubkey = <ephemeral pubkey>`,
   `content = <ciphertext>`, `tags = [['p', recipient]]` — just the single
   `p` tag, for every inner kind including offers. (An earlier draft of
   this doc claimed offers also carry a `['k', '25050']` tag, based on a
   stale copy of NosCall's source; the real, shipping
   `NipAcProtocol.wrap()` in NosCall 0.5.2-release never adds a `k` tag.)
   No seal layer (no kind-13 event).

**Unwrapping** a received kind-21059 event tagged `p = <our pubkey>`:
NIP-44-decrypt `content` using our real key and the wrap's `pubkey` (the
ephemeral sender key) as the counterparty, JSON-decode the result, verify
the *inner* event's own signature, and dispatch on its `kind`.

**Why this makes NosCall ring:** confirmed from `calling_controller.dart` —
receiving one valid, decodable offer (kind 25050, correctly wrapped, with
`call-type` present) addressed to NosCall's pubkey is the entire trigger.
The callee-side call controller's default state is `ringing`; there's no
additional handshake step before the incoming-call UI appears.

## Implementation

- `Signaling/CallSignalKinds.cs` — the five inner kinds plus `WrapKind = 21059`.
- `Signaling/NostrSignalingClient.cs` — one instance per call, constructed
  with the call's `call-id`. `PublishAsync` builds and signs the inner
  event with the bridge's real keys, wraps it with a freshly generated
  ephemeral keypair via `NostrSigner.Nip44Encrypt`, and publishes the outer
  event via `Client.SendEvent`. The receive path mirrors this in reverse
  and dispatches `answer`/`candidate`/`hangup`/`reject` events to the
  waiting call, after checking the inner event's `call-id` tag against the
  one this client was constructed with (the relay subscription filters on
  the bridge's pubkey, not on a call, so a second overlapping call's
  signaling would otherwise land here too - and a `hangup` for the wrong
  call now tears down a live one). An event with no `call-id` tag at all is
  still accepted: only a mismatch is evidence it belongs elsewhere.
- `Sip/SipCallSource.cs` — mints one `Guid.NewGuid()` call-id per inbound
  call (`Call.CallId`). `Sinks/NosCallSink.cs` passes it into
  `NostrSignalingClient`.
- `Sinks/ConnectionLossWatcher.cs` — a small, independently-testable class
  (no Nostr/SIP dependencies of its own) that debounces
  `RTCPeerConnectionState` into a single `WhenConnectionLost` task; see
  "Noticing a callee that vanishes mid-call" below for what it does with
  each state.

## Either side can end the call

A call ends when either leg says so, and the legs learn about it
differently. The SIP leg's hangup arrives as a `BYE`/`CANCEL` and
completes `Call.WhenRemoteHungUp` (see `docs/receiving-calls.md`). The
Nostr leg's arrives as a NIP-AC `hangup` (or a `reject`, if the callee
never answered) and completes `NostrSignalingClient.WhenCalleeHungUp`.
Once bridged, a third possibility is watched too: the WebRTC leg itself
going quiet with no signaling from either side, via
`ConnectionLossWatcher` (below). `NosCallSink` races all of these, at
whichever stage of a call applies:

- **While the callee's device is ringing:** a `hangup`/`reject` declines the
  call immediately rather than waiting out `ring_timeout_seconds`, so a
  declined call reaches `VoicemailSink` (or ends) straight away. sip2nostr
  doesn't send its own `hangup` back in that case — a device that just
  hung up doesn't need telling to stop ringing.
- **The caller gives up first, while the callee's device is still
  ringing:** sip2nostr sends its own `hangup`, the mirror of the
  ring-timeout case (`docs/voicemail.md`) — without it, nothing ever tells
  the callee's device the call is over, so it's left ringing at an empty
  line. Skipped if the callee had already ended it their own way at
  essentially the same moment, so as not to send a pointless hangup for a
  call NosCall already knows is done. `Call.WhenRemoteHungUp` completing
  doesn't distinguish a caller `CANCEL`/`BYE` from sipsorcery's own
  `MAX_RING_TIME` expiry, so the reason string sent is deliberately
  neutral ("call ended before it could be answered") rather than claiming
  the caller hung up, which wouldn't be true of the latter.
- **Local shutdown, while the callee's device is still ringing:** the same
  hangup goes out, best-effort, for the same reason — otherwise a restart
  leaves NosCall believing an abandoned call is still live. What decides
  whether this lands isn't the relay connection (open regardless at this
  point) but whether the publish gets to finish before the process exits —
  see "Cancellation without a join" below for how that's bounded.
- **Once audio is bridged, and the callee ends it:** a `hangup` tears the
  bridge down and returns, which is what makes `CallHub`'s own `finally`
  hang the SIP leg up with a `BYE`. Without this the caller is left on a
  silent, still-connected call.
- **Once audio is bridged, and the caller ends it:** the same hangup goes
  out to NosCall, guarded the same way as every other case here.
- **Once audio is bridged, and the callee's connection is simply lost —
  no `BYE`, no `hangup`, nothing:** `ConnectionLossWatcher` ends the call
  instead. See "Noticing a callee that vanishes mid-call" below.

## Noticing a callee that vanishes mid-call

A callee whose device force-quits or loses its network entirely can't
publish a `hangup` — there's nothing to react to. Once bridged,
`NosCallSink` subscribes `RTCPeerConnection.onconnectionstatechange` to a
`Sinks/ConnectionLossWatcher.cs` instance, which exposes a single
`WhenConnectionLost` task and races it alongside `Call.WhenRemoteHungUp`
and `WhenCalleeHungUp` in the same `Task.WhenAny`.

It isn't as simple as ending the call on the first `disconnected` state,
though: `RTCPeerConnectionState` legitimately flaps to `disconnected` on a
transient network hiccup (a brief Wi-Fi drop, a NAT rebind, a momentary
STUN consent-freshness check failing per RFC 7675) and often recovers to
`connected` again on its own. Reacting to that immediately would end calls
that would've been fine. So `ConnectionLossWatcher` debounces:

- **`disconnected`** starts a `ConnectionLossGraceSeconds` (15s) grace
  timer, unless one's already running — the state can flap several times
  in a row without restarting the clock.
- **`connected`** cancels a running grace timer — the connection
  recovered, nothing to see.
- **`failed`** completes `WhenConnectionLost` immediately, no grace
  needed. Per the WebRTC spec this state is only reached once the ICE
  agent has already exhausted its own connectivity checks and concluded
  it won't connect — it's sipsorcery's own terminal give-up, not a
  transient blip to wait out.
- The grace timer elapsing without a recovery also completes
  `WhenConnectionLost`.

When it fires, `NosCallSink` logs a warning (this is the one ending
neither leg announced) and still makes a best-effort attempt to publish a
`hangup` over Nostr — the WebRTC media path being unreachable doesn't
necessarily mean the Nostr relay connection is too; a TURN-specific
failure, say, wouldn't take a plain relay WebSocket down with it.

## Cancellation without a join

Sending a hangup is only as reliable as the time it's given to run.
`SipCallSource.HandleIncomingCall` discards the task for each inbound call
(`_ = HandleIncomingCallSafeAsync(...)`) — necessarily, since a SIP event
handler has to return promptly — and nothing downstream re-joins it:
`AcceptAndRouteCallAsync` awaits `OnIncomingCall`'s handlers properly,
which is `CallHub.HandleAsync`, which awaits each sink in turn. So the
whole call, from ringing through whichever sink handles it through this
sink's own shutdown-path hangup, hangs off that one discarded task -
`Program.cs`'s `cts.Cancel()` tells it to stop via the shared
`CancellationToken`, then proceeds straight to tearing down `source` and
`voicemailSender` and flushing the log, whether or not that task has
actually finished.

`CallHub.DrainAsync(TimeSpan grace)` closes that gap: `Attach` now tracks
every `HandleAsync` task it starts in a `ConcurrentDictionary`, and
`Program.cs` calls `DrainAsync` right after its own shutdown `Task.Delay`
returns - before `source`/`voicemailSender` are disposed, before the log
is flushed - so an in-flight call gets up to `grace` (5s) to finish
publishing its hangup, or a `VoicemailSink` recording gets a chance to
finish saving, before the resources it depends on go away underneath it.
A no-op when nothing's in flight, which is the common case for a shutdown
that isn't racing an active call.

The wrap/unwrap logic was first verified locally with a round-trip test
(two throwaway keypairs, no network): build and sign an offer as the
bridge, wrap it, decrypt and unwrap it as the target, confirm the SDP
content and tags survive intact, the inner signature verifies, and a
third key cannot decrypt the payload. It has since been confirmed against
a real relay and a real NosCall install, ringing and carrying audio both
ways.

## Blind spots

- **TURN/NAT behavior for the WebRTC leg is untested** beyond the local
  network the verification above ran on.
- **No staleness check.** NIP-AC recommends discarding signaling events
  older than 20 seconds (by `created_at`) to avoid phantom calls from
  stale relay-cached events on reconnect. Not implemented.
- **No event-ID deduplication.** The same wrapped event delivered by
  multiple relays isn't currently de-duplicated.
- **No self-event filtering.** NIP-AC's multi-device support relies on
  clients ignoring their own echoed ICE/hangup events and only accepting
  self-addressed answer/reject in specific states. Not relevant to a
  single-instance bridge today, but worth knowing if that ever changes.
  (Events not authored by `target_npub` are dropped regardless, so the
  bridge's own echoed events are never acted on.)
- **A callee vanishing without signaling is noticed with a delay, not
  instantly.** `ConnectionLossWatcher` (see "Noticing a callee that
  vanishes mid-call") only reacts to `RTCPeerConnectionState`, which is
  itself a local, best-effort judgment sipsorcery makes from ICE
  connectivity checks — a callee that stays technically reachable but
  stops sending/receiving media wouldn't necessarily trip it. And the
  `ConnectionLossGraceSeconds` debounce is a deliberate trade of
  promptness for not dropping calls on a transient blip, not a claim that
  15s is the right number for every network this runs on.
- **No busy/reject signaling sent.** If sip2nostr is somehow mid-call
  already, it doesn't auto-reject a second offer the way NIP-AC recommends.
- **No multi-device self-notification.** Not applicable — sip2nostr is a
  single bridge identity, not a multi-device NosCall user.
- **Group calls (multiple `p` tags) are out of scope.** sip2nostr only
  ever targets the single configured `target_npub`.
