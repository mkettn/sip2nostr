# Voicemail (Answer-Timeout Fallback)

This documents the answering-machine behavior added to close the
`receiving-calls.md` blind spot "No answer-timeout fallback": previously,
if `target_npub` never answered a call over Nostr, the call was simply
left connected to silence until the caller hung up. `[voicemail]` is
**opt-in** - `enabled = false` by default, so out of the box nothing
changes. With it turned on, a caller who isn't answered within
`ring_timeout_seconds` instead hears a greeting, then gets recorded for up
to `max_recording_seconds`, and the recording is sent to `target_npub` as
a Nostr direct message.

Not yet verified against a real SIP trunk or a real receiving Nostr
client - implemented from the same sipsorcery/Nostr.Sdk APIs already
verified working elsewhere in this codebase (see below), but the feature
itself hasn't been exercised end-to-end yet.

## Flow

```
 CallBridge.HandleIncomingCallAsync (Nostr enabled)
      │
      ├─ SIP call answered immediately, same as today
      ├─ WebRTC offer sent over Nostr, same as today
      │
      ▼
 Wait for: Nostr answer | ring_timeout_seconds elapses | caller hangs up | signaling fails
      │
      ├─ Nostr answers in time  → bridge audio, unchanged from today
      ├─ Caller hangs up first  → tear down, unchanged from today
      │
      └─ Timeout or signaling failure → voicemail fallback:
           1. Send a NIP-AC `hangup` over Nostr so a ringing device (e.g.
              NosCall) stops ringing (best-effort; failure is logged, not
              fatal) - the bridge originated this call, so giving up on it
              is a hangup, not a reject (the callee's decline signal).
           2. Close the WebRTC peer connection; the already-answered SIP
              RTP session stays up and is reused directly.
           3. Play `greeting_sound` once (or a short tone if unset).
           4. Record caller audio for up to `max_recording_seconds`, or
              until they hang up.
           5. Save the recording as a WAV file under `recordings_dir`
              (always - this is the durability point, independent of
              whatever happens to the send afterward).
           6. Hang up the SIP call (`ua.Hangup()`) - immediately, without
              waiting on delivery. Always runs even if step 5 threw (e.g.
              a bad `greeting_sound` path, a disk error), so a failure
              there can't leave the caller on a silent, still-connected
              call or leak the RTP session.
           7. Hand the WAV off to VoicemailSender (Voicemail/VoicemailSender.cs)
              and move on - encoding and delivery happen off this call's
              critical path, in a separate background worker. See below.
```

VoicemailSender then, independently of any particular call:

```
 VoicemailSender (one instance, shared for the process lifetime)
      │
      ▼
 Idle, waiting on the queue - no relay connection held open
      │
      │  a WAV path arrives (queue was empty until now)
      ▼
 Connect once to dm_relays (or [nostr].relays as a fallback)
      │
      ├─ drain everything queued at this point (a backlog of several
      │  voicemails costs one connect, not one per voicemail)
      │
      └─ for each: re-encode as Opus/OGG in-process via Concentus (pure
         C#, no external program - falls back to sending the WAV
         directly if encoding fails for any reason), then send as a
         NIP-17 private direct message. A relay rejecting the event
         (e.g. too large) is detected and logged as a failure, not
         reported as sent.
      │
      ▼
 Disconnect, go back to idle
```

## Implementation

- `Sip/CallBridge.cs`:
  - `HandleIncomingCallAsync` races the existing `WaitForAnswerAsync` Nostr
    call against `Task.Delay(ring_timeout_seconds)` and the caller-hangup
    signal. A signaling exception (e.g. no relay reachable) triggers the
    same fallback as a timeout, not just an explicit timeout - the point
    is "this call is not going to be answered over Nostr", not literally
    just the clock.
  - `BridgeAudio` gained a `forwardSipToWebRtc` gate on the SIP-leg →
    WebRTC-leg direction only, so falling back to voicemail can stop
    relaying into the (now closed) `RTCPeerConnection` without needing to
    unsubscribe an anonymous event handler.
  - `RunVoicemailAsync` reuses the **already-answered SIP `RTPSession`**
    directly instead of building a second media session: a manually wired
    `SIPSorcery.Media.AudioExtrasSource` plays the greeting/tone
    (`SendAudioFromStream`, the same mechanism `[[lines]].sound` already
    uses via `AudioSendOnlyMediaSession`, just wired to a plain
    `RTPSession` here since the SIP dialog is already up), while a second
    `OnRtpPacketReceived` subscriber decodes inbound caller audio via
    `SIPSorcery.Media.AudioEncoder.DecodeAudio` and buffers it.
  - `WavEncoder` (new, pure logic, unit tested) writes a minimal canonical
    16-bit PCM WAV header around the buffered samples.
  - The final `ua.Hangup()` in `HandleIncomingCallAsync` runs in a
    `finally` around the `RunVoicemailAsync` call, and `SIPUserAgent`'s own
    `MediaSession` field is the same `RTPSession` instance
    `RunVoicemailAsync` recorded on - so `ua.Hangup()` both sends the BYE
    and closes the RTP session in one call; it's also safe to call
    unconditionally (it checks `IsCallActive` internally, so it's a no-op
    if the caller already hung up). `RunVoicemailAsync` only ever writes
    the WAV and calls `VoicemailSender.Enqueue` - it has no Nostr.Sdk
    dependency at all, so nothing in the call-handling path blocks on
    relay connectivity or a publish.
  - `[voicemail].ring_timeout_seconds` / `max_recording_seconds` are
    validated (`> 0`) in `Config/ConfigLoader.cs` at startup, alongside
    the rest of config loading - an unchecked bad value would otherwise
    surface deep inside `Task.Delay` as every call being silently routed
    to voicemail with a misleading "signaling failed" log line.
    `max_recording_seconds` is also rejected there if it exceeds
    `Voicemail/VoicemailBudget.cs`'s `MaxRecordingSeconds` - the largest
    value that can still fit in a NIP-17 DM (see Blind spots below) - so
    a value that can never be delivered fails at startup rather than only
    after a caller has already left an undeliverable message.
- `Voicemail/VoicemailSender.cs` (new): one instance, constructed once in
  `BridgeService.StartAsync` and shared across every call for the life of
  the process - unlike `NostrSignalingClient`, which is scoped to a
  single call.
  - `Enqueue` writes to an unbounded `System.Threading.Channels.Channel`
    and returns immediately; it's a plain in-memory queue (multiple calls
    can enqueue concurrently - `Channel` is built for that), not a
    persistent one, so anything still queued at process shutdown is
    logged as undelivered but its WAV is safely already on disk.
  - The worker loop (`RunAsync`, started from the constructor) blocks on
    `Channel.Reader.WaitToReadAsync` while the queue is empty - no relay
    connection, no timer. On the first item it drains everything
    currently queued into one batch, connects one `Client` (a fresh
    instance per batch, deliberately not shared with any call's
    `NostrSignalingClient` - so its own relay pool, and `TryConnect`'s
    reachability result, is never polluted by relays something else
    already had connected), sends the whole batch, then shuts that
    `Client` down and goes back to waiting.
  - Per voicemail: re-encodes to Opus/OGG entirely in-process via
    `Concentus` (a pure C# port of libopus) and `Concentus.Oggfile`
    (writes the Ogg container - `OpusOggWriteStream` - around the
    encoded packets), falling back to sending the WAV directly if
    encoding fails for any reason. `WavEncoder`'s header is a fixed,
    known 44 bytes, so the PCM samples are read back with a plain
    `Buffer.BlockCopy` rather than a general WAV parser. Deliberately
    not `ffmpeg`/any external process - this keeps the whole feature
    working in a self-contained single-file binary with nothing to
    install on the host, and there's no intermediate `.ogg` file on
    disk at all (`OpusOggWriteStream` writes straight into a
    `MemoryStream`). Then calls `Client.SendPrivateMsgTo(relayUrls, ...)` - NIP-17: rumor,
    seal, gift wrap, and publish all handled by `Nostr.Sdk` - targeting
    exactly the relay set (`[voicemail].dm_relays`, or `[nostr].relays`
    as a fallback) this batch just connected to. `SendPrivateMsgTo` is
    used explicitly rather than plain `SendPrivateMsg` so the relays
    published to are always the same ones just verified reachable,
    rather than Nostr.Sdk's own separate NIP-17 relay-discovery landing
    on a different set. Unlike the NIP-AC signaling wrap `NostrSignalingClient`
    uses for call offer/answer/ICE, a voicemail should be readable by any
    NIP-17-capable client, not just NosCall. The returned
    `SendEventOutput` is checked (shared `PublishOutcome.ThrowIfFailed`
    helper, also used by `NostrSignalingClient.PublishAsync`) - a relay
    can reject an event with `OK: false` without the publish call itself
    throwing, so this is the only way to actually detect it. A failure
    for one voicemail in a batch is caught and logged per-voicemail; it
    doesn't stop the rest of the batch from being attempted.

## Blind spots

- **Audio is inlined as a base64 `data:` URI directly in the DM content,
  not uploaded to a file host - and this hard-caps recording length far
  below what a minute-long voicemail needs.** sip2nostr has no
  NIP-96/Blossom upload dependency today, so the entire recording has to
  fit inside one Nostr message. Two separate limits stack against it:
  - **The relay's max event size** (commonly 64-256 KB) - the blind spot
    this was originally framed around.
  - **NIP-44's own plaintext cap, before any relay is even involved -
    and the tighter of the two.** A NIP-17 DM is NIP-44-encrypted twice
    (sealing the rumor, then wrapping the seal), and NIP-44 v2 caps
    plaintext at 65,535 bytes and pads it into fixed-size buckets before
    encrypting (see nips.nostr.com/44's `calc_padded_len`) - encryption
    itself fails past that, not just delivery. Working back from the
    outer (gift wrap) layer's cap to how much raw audio that leaves room
    for:
    ```
      65,535   NIP-44 plaintext cap (gift wrap layer)
    -    490   seal event JSON overhead (id/pubkey/tags/sig/...)
    ÷    4/3   undo base64 on the seal's own ciphertext
    ≈ 48,784   budget for the seal's padded ciphertext
    → 40,960   largest padded length that actually fits (calc_padded_len's
               next bucket up is 49,152, which doesn't fit in 48,784 - so
               ~16% of the naive budget is lost to bucket rounding)
    -    430   rumor JSON overhead (kind/tags/created_at/...) + prose
    ÷    4/3   undo base64 on the audio data URI
    ≈ 30,400   bytes of raw (pre-base64) encoded audio - MaxAudioBytes
    ```
    `Voicemail/VoicemailBudget.cs` holds this as `MaxAudioBytes` (30,400)
    and derives `MaxRecordingSeconds` (30, at the current 8 kbps Opus
    encoding) from it; `ConfigLoader` rejects a configured
    `max_recording_seconds` above that at startup (see Implementation
    above) rather than letting a caller leave a message that can never
    be delivered. The shipped default (`max_recording_seconds = 30`,
    `config.example.toml`) sits exactly at that ceiling. Raising it
    requires either a lower bitrate (diminishing returns - 8 kbps is
    already conservative for 8 kHz telephony audio) or a real upload
    path (data URI → uploaded file + `imeta`/`url` tag) to remove the
    cap entirely - the latter is the actual fix; this budget is a
    ceiling this architecture can't grow past.
- **DTX (encoder silence-dropping) is not available, so the size problem
  above can't currently be helped by compressing the silence out of a
  recording.** `Concentus.Oggfile`'s `OpusOggWriteStream` - the Ogg
  container writer `VoicemailSender` uses - unconditionally rejects a
  DTX-enabled encoder at construction (`ArgumentException("DTX is not
  currently supported in Ogg streams")`, confirmed by reading its
  source). Enabling `IOpusEncoder.UseDTX` would make every encode throw
  and fall back to sending the far larger raw WAV - the opposite of the
  goal - so it's deliberately left off (see the comment in
  `VoicemailSender.TryEncodeOpusOgg`). Revisiting this needs either a
  different (DTX-aware) Ogg writer or hand-rolling the Ogg container
  framing to tolerate the granule-position gaps DTX produces.
- **No silence/VAD trimming or beep tone.** Recording starts immediately
  after the greeting/tone finishes and runs for the full
  `max_recording_seconds` (or until hangup) regardless of whether the
  caller is actually speaking.
- **DTMF-triggered early hangup, "press 1 to skip greeting", etc. are not
  implemented** - matches the existing "DTMF is explicitly disabled"
  blind spot in `receiving-calls.md`.
- **Concurrent calls are untested**, same caveat as the rest of the
  SIP/RTP path per `receiving-calls.md`.
- **The send queue is in-memory only, not persisted across restarts.** A
  voicemail recorded and enqueued but not yet sent when the process is
  stopped is lost from the queue (though its WAV file on disk is not -
  it just won't be retried automatically; resending it would need to be
  done by hand, there's no "scan `recordings_dir` for orphaned WAVs on
  startup" logic). For a personal single-line deployment where the
  process runs continuously, this is a minor gap; it would matter more
  under frequent restarts or heavy call volume.
- **Not yet verified against a real SIP trunk or a real NIP-17 client.**
