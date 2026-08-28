# Voicemail (Answer-Timeout Fallback)

This documents the answering-machine behavior added to close the
`receiving-calls.md` blind spot "No answer-timeout fallback": previously,
if `target_npub` never answered a call over Nostr, the call was simply
left connected to silence until the caller hung up. `[voicemail]` is
**opt-in** - `enabled = false` by default, so out of the box nothing
changes. With it turned on, a caller who isn't answered within
`ring_timeout_seconds` hears a greeting and gets recorded for up to
`max_recording_seconds`. Exactly one Nostr direct message goes to
`target_npub` per missed call: the recording, if anything worth sending
was captured, otherwise a plain-text missed-call notice - never both,
never neither.

Verified end-to-end against a real SIP trunk and a real NIP-17 client,
for both delivery backends: a call that falls back to voicemail plays
the greeting/tone, records the caller, and saves the recording as
Opus/OGG, then either delivers it as a NIP-17 DM that the receiving
client decrypts and plays back correctly (`delivery = "audio"`), or
transcribes it and delivers the transcript as DM text (`delivery =
"text"`). The
`MissedCallNoticeJob` path (recording too short / caller hangs up
before anything is captured) hasn't specifically been exercised, but
shares the same delivery code as the verified `VoicemailAudioJob` path.

## Flow

Since the hub-architecture refactor (see `docs/hub-architecture.md`), the
ring-timeout race lives in `NosCallSink` and the recording flow lives in
`VoicemailSink` - two `ICallSink`s tried in order by `CallHub` for the same
already-answered `Call`, rather than one function doing both.

```
 CallHub, routing a Call from SipCallSource (Nostr enabled)
      │
      ├─ SIP call already answered by SipCallSource before CallHub sees it
      │
      ▼
 NosCallSink.TryHandleAsync
      ├─ WebRTC offer sent over Nostr, same as today
      │
      ▼
 Wait for: Nostr answer | ring_timeout_seconds elapses | caller hangs up | signaling fails
      │
      ├─ Nostr answers in time  → bridge audio, returns true (handled)
      ├─ Caller hangs up first  → tear down, returns true (handled)
      │
      └─ Timeout or signaling failure → decline (return false); CallHub
         offers the Call to the next configured sink, VoicemailSink:
           1. Close the WebRTC peer connection; the already-answered SIP
              leg's Call.Audio stays up and is reused directly - no new
              media session is created.
           2. Send a NIP-AC `hangup` over Nostr so a ringing device (e.g.
              NosCall) stops ringing (best-effort; failure is logged, not
              fatal) - the bridge originated this call, so giving up on it
              is a hangup, not a reject (the callee's decline signal).
           3. Play `greeting_sound` once (or a short tone if unset). If the
              caller hangs up here, log it (caller number included) and
              enqueue a MissedCallNoticeJob on VoicemailSender - nothing
              was recorded, but it's still a missed call.
           4. Record caller audio for up to `max_recording_seconds`, or
              until they hang up.
           5. Encode the recording to Opus/OGG (in-process via
              `Sip/OggOpusCodec.cs`) and save it under `recordings_dir`
              (always - this is the durability point, independent of
              whatever happens to the send afterward).
           6. Return true (handled) - CallHub's own `finally` calls
              `Call.HangupAsync` unconditionally once a sink is done, so
              VoicemailSink doesn't need to hang up the SIP call itself.
              This always runs even if step 5 threw (e.g. a bad
              `greeting_sound` path, a disk error), so a failure there
              can't leave the caller on a silent, still-connected call or
              leak the RTP session.
           7. If the recording is long enough to be worth sending, enqueue
              a VoicemailAudioJob (the Ogg/Opus path) on VoicemailSender
              and move on - delivery happens off this call's critical
              path, in a separate background worker. Otherwise (too
              short), log it and enqueue a MissedCallNoticeJob instead -
              the same "exactly one job" rule as step 3.
           8. If RunVoicemailAsync throws instead of reaching step 7 (a
              bad greeting_sound path, a disk error or Opus encoding
              failure saving the recording), VoicemailSink.TryHandleAsync's
              own catch around the call enqueues a MissedCallNoticeJob -
              so a misconfiguration still notifies target_npub instead of
              silently dropping the caller with nothing to show for it
              anywhere but a log line.
```

Exactly one job is enqueued per call that reaches the voicemail
fallback: a `MissedCallNoticeJob` wherever recording didn't produce
anything worth sending (including `RunVoicemailAsync` throwing), or a
`VoicemailAudioJob` if it did. The two are mutually exclusive, so
`target_npub` never gets both a notice and a voicemail for the same
call - and never neither.

VoicemailSender then, independently of any particular call:

```
 VoicemailSender (one instance, shared for the process lifetime)
      │
      ▼
 Idle, waiting on the queue - no relay connection held open
      │
      │  a job arrives (queue was empty until now) - either a
      │  MissedCallNoticeJob or a VoicemailAudioJob
      ▼
 Connect once to dm_relays (or [nostr].relays as a fallback)
      │
      ├─ drain everything queued at this point (a backlog of several
      │  jobs costs one connect, not one per job)
      │
      └─ for each job: MissedCallNoticeJob → plain-text content;
         VoicemailAudioJob → delivery-backend-dependent content built
         from the already-Opus/OGG-encoded recording on disk (encoded at
         record time by VoicemailSink, not here - see Delivery backends
         below). Either way, send as a NIP-17 private direct message. A
         relay rejecting the event (e.g. too large) is detected and
         logged as a failure, not reported as sent.
      │
      ▼
 Disconnect, go back to idle
```

## Delivery backends

How a recorded voicemail becomes DM content is pluggable via
`[voicemail].delivery`:

- `"audio"` (default) - `Voicemail/AudioInlineDeliveryBackend.cs` reads
  the recording (already Opus/OGG - `VoicemailSink` encodes it when
  saving, not this backend) and inlines it as a base64 `data:` URI in the
  DM content, subject to the NIP-17 size budget covered above.
- `"text"` - `Voicemail/TranscribedTextDeliveryBackend.cs` transcribes
  `VoicemailAudioJob.Samples` - the original recorded PCM, carried on the
  job alongside `OggPath` rather than decoded back out of the saved
  Opus/OGG file - via a speech-to-text engine, sending the transcript as
  plain text instead. Transcribing the original PCM instead of a
  lossy-recompressed copy avoids feeding whisper.cpp audio that's already
  been through 8 kbps Opus once. This path isn't bound by the Opus/NIP-17
  budget above, so it has its own two checks: `max_recording_seconds` is
  capped at
  `VoicemailBudget.MaxTextRecordingSeconds` (600s) instead of
  `VoicemailBudget.MaxRecordingSeconds` (see `Config/ConfigLoader.cs`) -
  a sanity ceiling on how much PCM `VoicemailSink` buffers in memory
  while recording, not a size budget - and the transcript itself is
  checked against `VoicemailBudget.MaxTranscriptBytes` (40,000 bytes) at
  send time, mirroring `AudioInlineDeliveryBackend`'s `MaxAudioBytes`
  check. A transcript is normally tiny compared to that budget, but
  whisper.cpp can fall into a repetition loop on silence or noise and
  produce far more text than any real voicemail would, so the check
  guards against that rather than being trusted to never trigger. If
  nothing could be transcribed (silence, an engine failure), the backend
  sends a plain-text notice instead of an empty message.

Both implement `Voicemail/IVoicemailDeliveryBackend.cs`
(`BuildContentAsync(VoicemailAudioJob, CancellationToken) -> (Content,
Tags, Description)`) - the only thing `VoicemailSender` depends on; it
doesn't know or care which backend it's holding, and owns disposing it
alongside its own worker.

### Speech-to-text engine

The `"text"` backend's actual transcription sits behind a second,
independently swappable interface, `Voicemail/IVoicemailTranscriber.cs`
(`TranscribeAsync(short[] samples, int sampleRate, CancellationToken) ->
string?`, `null` meaning nothing could be transcribed), selected by
`[voicemail.transcription].engine`:

- `"whisper"` (the only engine today) - `Voicemail/WhisperNetTranscriber.cs`
  runs [Whisper.net](https://github.com/sandrohanea/whisper.net) (a
  whisper.cpp binding) fully offline: no network access and no API key
  at transcription time, just a local GGML model file
  (`[voicemail.transcription].model_path`, required when
  `delivery = "text"` - `ConfigLoader` checks the file exists at
  startup). whisper.cpp expects 16 kHz mono float samples in `[-1, 1]`;
  `TranscribedTextDeliveryBackend` hands over `VoicemailAudioJob.Samples`
  as recorded - 8 kHz PCM (G.711's rate) - so `WhisperNetTranscriber`
  resamples via `SIPSorcery.Media.PcmResampler`
  (already a project dependency, so no new one is needed just for that)
  and converts to `float` before handing samples to Whisper.
  `[voicemail.transcription].language` pins the spoken language (e.g.
  `"en"`); left unset, Whisper auto-detects it per recording.

## Implementation

- `Sinks/NosCallSink.cs`:
  - `TryHandleAsync` races the existing `WaitForAnswerAsync` Nostr call
    against `Task.Delay(ringTimeoutSeconds)` and `Call.WhenRemoteHungUp`.
    `ringTimeoutSeconds` is `null` unless `[voicemail].enabled` (wired up
    in `Program.cs`), so with voicemail disabled this sink rings
    indefinitely instead of timing out, matching pre-voicemail behavior.
    A signaling exception (e.g. no relay reachable) triggers the same
    decline as a timeout, not just an explicit timeout - the point is
    "this call is not going to be answered over Nostr", not literally
    just the clock. After `Task.WhenAny` returns, three checks run in a
    deliberate order - `ct.IsCancellationRequested`, then
    `call.WhenRemoteHungUp.IsCompleted`, then `answerTask.IsCompleted` -
    because `ringTimeoutTask` is cancelled by the same `ct` as a shutdown,
    so which of `ringTimeoutTask` and `call.WhenRemoteHungUp` `WhenAny`
    happens to observe as complete first isn't a reliable way to tell a
    shutdown apart from a genuine ring timeout; `ct.IsCancellationRequested`
    is a plain synchronous read with no such ambiguity. Checking
    `answerTask` itself last (rather than trusting which task `WhenAny`
    reported as the winner) then gives a genuine answer priority if it
    lands at the same moment the ring timeout elapses.
  - Bridges `Call.Audio` and its own `RtpSessionCallAudio` (wrapping the
    `RTCPeerConnection`, restricted to the same `Call.AudioFormat` as the
    SIP leg) by forwarding RTP frames unchanged each way - the same raw
    relay the pre-hub implementation used, just moved out of `CallBridge`
    (see `docs/hub-architecture.md` for why the hub itself stays
    RTP-shaped rather than PCM). Declining (returning `false`)
    unsubscribes both forwarding handlers before returning, so a fallback
    to `VoicemailSink` can't keep relaying audio into the (now closed)
    `RTCPeerConnection` - no `Volatile` gate needed, since
    `ICallAudio.OnAudioReceived` is a plain event and the handlers are
    named delegates that can be removed directly.
- `Sinks/VoicemailSink.cs`:
  - `RunVoicemailAsync`'s `recordingActive` flag is read on the thread
    delivering `Call.Audio.OnAudioReceived` callbacks and written on the
    sink's own async flow; both sides go through the same lock, since
    without one there's no guarantee the receiving side ever observes the
    write.
  - Reuses the **already-answered `Call.Audio`** directly instead of
    building a second media session: a `SIPSorcery.Media.AudioExtrasSource`
    plays the greeting/tone, wired via `OnAudioSourceEncodedSample +=
    call.Audio.SendEncodedSample` instead of the pre-hub code's `+=
    sipMediaSession.SendAudio` - same off-the-shelf player, same signature
    (`ICallAudio.SendEncodedSample` exists specifically to match it), just
    retargeted at the hub's `ICallAudio` instead of a concrete
    `RTPSession`. An earlier version of this sink hand-rolled its own PCM
    pacing/RTP-framing loop instead; that reimplemented exactly what
    `AudioExtrasSource` already does correctly (a monotonic timestamp, a
    real-time-paced send loop, marker bits) and got two of the three
    wrong - see the PR review that caught it. `AudioExtrasSource` needs a
    real file to stream from, so the sound-file path from
    `SoundFileResolver.Resolve` is opened directly (`File.OpenRead` +
    `SendAudioFromStream`) rather than loaded into a `short[]` first, and
    the no-greeting-configured case now uses `SetSource(SineWave)` instead
    of a hand-generated tone. Meanwhile an `OnAudioReceived` subscriber
    decodes each inbound frame via
    `SIPSorcery.Media.AudioEncoder.DecodeAudio` and buffers the PCM - the
    hub itself only ever hands over RTP frames, so this sink is the one
    that knows how to turn them into samples.
  - `SaveRecordingAsync` encodes the buffered PCM samples to Opus/OGG via
    `Sip/OggOpusCodec.Encode` (pure logic, unit tested; also used to
    decode `[[lines]].sound`/`[voicemail].greeting_sound` files in
    `Shared/SoundFileResolver.cs`) before writing the result under
    `recordings_dir` - so the file on disk is already exactly what
    `delivery = "audio"` sends, with no separate re-encode step at
    delivery time. `OggOpusCodec.Encode` runs with `UseVBR = false`:
    Concentus (like libopus) defaults to VBR, where `Bitrate` is only a
    target the encoder can exceed on complex input, which would
    undermine the size budget `AudioInlineDeliveryBackend` checks the
    saved file against (see Blind spots below). Its `OpusOggWriteStream`
    deliberately has no `using` - it isn't `IDisposable`; `Finish()` is
    what pads the trailing frame, writes the end-of-stream page, and
    flushes, and `leaveOpen` keeps the underlying `MemoryStream` readable
    afterwards.
  - `TryHandleAsync` always returns `true`: `CallHub`'s own `finally`
    calls `Call.HangupAsync` unconditionally once a sink is done, so this
    sink doesn't hang up the SIP call itself and doesn't need a `finally`
    of its own to guarantee that happens even if `RunVoicemailAsync`
    throws. `RunVoicemailAsync` only ever writes the Opus/OGG recording
    and calls `VoicemailSender.Enqueue` - it has no Nostr.Sdk dependency
    at all, so nothing in the call-handling path blocks on relay
    connectivity or a publish.
  - `[voicemail].ring_timeout_seconds` / `max_recording_seconds` are
    validated (`> 0`) in `Config/ConfigLoader.cs` at startup, alongside
    the rest of config loading - an unchecked bad value would otherwise
    surface deep inside `Task.Delay` as every call being silently routed
    to voicemail with a misleading "signaling failed" log line.
    `max_recording_seconds` is also rejected there if it exceeds
    `Shared/VoicemailBudget.cs`'s `MaxRecordingSeconds` - a value that
    reliably fits a NIP-17 DM with headroom to spare (see Blind spots
    below) - so a value that can never be delivered fails at startup
    rather than only after a caller has already left an undeliverable
    message. `AudioInlineDeliveryBackend.BuildContentAsync` separately
    checks the *actual* encoded size against `VoicemailBudget.MaxAudioBytes`
    before every send, since `MaxRecordingSeconds` is a heuristic ceiling
    on the configured value, not a guarantee about what any given
    recording encodes to.
- `Voicemail/VoicemailSender.cs`: one instance, constructed once in
  `Program.cs` and shared across every call for the life of the process -
  unlike `NostrSignalingClient`, which is scoped to a single call.
  - `Enqueue` takes a `Voicemail/SendJob.cs` - either a `MissedCallNoticeJob`
    (`CallerNumber`, `CallId`, no audio) or a `VoicemailAudioJob` (adds
    `OggPath`, `Samples`, `SampleRate`, `DurationSeconds` - both the saved
    Opus/OGG file and the original recorded PCM, so each delivery backend
    reads whichever it actually needs) - and writes it to an
    unbounded `System.Threading.Channels.Channel<SendJob>`, returning
    immediately. It's a plain in-memory queue (multiple calls can enqueue
    concurrently - `Channel` is built for that), not a persistent one, so
    anything still queued at process shutdown is logged as undelivered;
    a `VoicemailAudioJob`'s Opus/OGG recording is safely already on disk
    regardless, but a dropped `MissedCallNoticeJob` has nothing else
    backing it up.
  - The worker loop (`RunAsync`, started from the constructor) blocks on
    `Channel.Reader.WaitToReadAsync` while the queue is empty - no relay
    connection, no timer. On the first item it drains everything
    currently queued into one batch, connects one `Client` (a fresh
    instance per batch, deliberately not shared with any call's
    `NostrSignalingClient` - so its own relay pool, and `TryConnect`'s
    reachability result, is never polluted by relays something else
    already had connected), sends the whole batch, then shuts that
    `Client` down and goes back to waiting. `SendBatchAsync`'s own setup
    (parsing keys/relays, connecting, shutting the `Client` down) is
    guarded separately from each individual send (`SendOneSafeAsync`
    guards those) - without it, a failure there (a bad relay URL, a
    connect exception) would escape to `RunAsync`'s outer catch and
    permanently stop the worker for the rest of the process, silently
    dropping every job enqueued afterwards despite each still being
    logged as "queued". A batch that fails this way is not retried
    automatically - only the worker itself survives, ready for the next
    `Enqueue` - though a `VoicemailAudioJob`'s Opus/OGG recording stays
    safe on disk regardless; a `MissedCallNoticeJob` has nothing else
    backing it up.
  - `BuildMissedCallNoticeContent` builds a short plain-text DM naming
    `CallerNumber`; no encoding, no size check needed - it's well under
    any NIP-17 budget.
  - Per `VoicemailAudioJob`: delegates to the configured
    `IVoicemailDeliveryBackend` (see Delivery backends above) rather than
    encoding anything itself - the recording is already Opus/OGG on disk
    by the time `VoicemailSender` ever sees the job (`Sinks/VoicemailSink.cs`
    encodes it at record time; deliberately not `ffmpeg`/any external
    process, keeping the whole feature working in a self-contained
    single-file binary with nothing to install on the host).
    `AudioInlineDeliveryBackend.BuildContentAsync` checks the file's
    actual size against `VoicemailBudget.MaxAudioBytes` before anything
    is sent - see Blind spots below - throwing rather than attempting a
    send that can only fail deep inside NIP-44 encryption or at the
    relay. Then `VoicemailSender`
    calls `Client.SendPrivateMsgTo(relayUrls, ...)` - NIP-17: rumor,
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
    for one job in a batch is caught and logged per-job (the Ogg/Opus
    path is included for a `VoicemailAudioJob`); it doesn't stop the rest
    of the batch from being attempted.

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
    `Shared/VoicemailBudget.cs` holds this as `MaxAudioBytes` (30,400) -
    the hard ceiling `AudioInlineDeliveryBackend.BuildContentAsync` checks
    the actual encoded output against before every send. `MaxRecordingSeconds`,
    used for the shipped default and startup validation, is deliberately
    *not* `MaxAudioBytes / (bitrate / 8)`: that naive division assumes
    the encoder produces exactly the target bitrate and ignores the Ogg
    container itself. Verified empirically (a throwaway encode of a 30s
    tone at 8 kbps with VBR off): actual output ran ~1,050-1,060
    bytes/sec against a naive estimate of 1,000 - a 30s recording came
    out to ~31.8 KB, over budget despite "fitting" the naive math. 10%
    of `MaxAudioBytes` is reserved for that overhead plus slack for
    `CallerNumber` (bounded by `PhoneNumberNormalizer` at 32 digits -
    generous headroom over E.164's 15-digit max, but still enough that
    an unbounded, possibly malicious From-header can't eat arbitrarily
    into the fixed rumor-overhead assumed above), giving `MaxRecordingSeconds` =
    27 at the current 8 kbps encoding; `ConfigLoader` rejects a
    configured `max_recording_seconds` above that at startup (see
    Implementation above). The shipped default (`max_recording_seconds =
    27`, `config.example.toml`) sits exactly at that ceiling, with the
    real per-recording enforcement against `MaxAudioBytes` as a backstop
    in case any single recording still runs over. Raising the ceiling
    further requires either a lower bitrate (diminishing returns - 8
    kbps is already conservative for 8 kHz telephony audio) or a real
    upload path (data URI → uploaded file + `imeta`/`url` tag) to remove
    the cap entirely - the latter is the actual fix; this budget is a
    ceiling this architecture can't grow past.
- **No fallback if Opus encoding fails when a recording is saved.**
  Since the recording is encoded to Opus/OGG at record time
  (`VoicemailSink.SaveRecordingAsync`, via `Sip/OggOpusCodec.Encode`),
  an encoding failure there means nothing is saved to disk at all, not
  just undelivered - `RunVoicemailAsync` throws, and
  `VoicemailSink.TryHandleAsync`'s own catch sends a `MissedCallNoticeJob`
  instead (see Flow step 8). An earlier version encoded at delivery time
  instead and fell back to sending the raw WAV recording when encoding
  failed there, but that fallback could never actually succeed for a
  real voicemail: 8kHz 16-bit mono WAV runs 16,000 bytes/sec, so
  `MaxAudioBytes` (30,400) only fits a 1.0-1.9s WAV, and recordings under
  1.0s are already dropped before encoding is ever attempted - so the
  fallback was dead weight that just moved the failure later (an opaque
  error inside `SendPrivateMsgTo`) instead of avoiding it. A genuine
  encoding failure is not expected in normal operation (no I/O, no
  external process - see DTX below for the one known throw path, which
  is deliberately never triggered), but unlike the delivery-time failure
  this one costs the recording itself, not just the send.
- **DTX (encoder silence-dropping) is not available, so the size problem
  above can't currently be helped by compressing the silence out of a
  recording.** `Concentus.Oggfile`'s `OpusOggWriteStream` - the Ogg
  container writer `Sip/OggOpusCodec.Encode` uses - unconditionally
  rejects a DTX-enabled encoder at construction (`ArgumentException("DTX
  is not currently supported in Ogg streams")`, confirmed by reading its
  source). Enabling `IOpusEncoder.UseDTX` would make every encode throw,
  and with no fallback if that happens (see above) that means the
  recording is lost, not just undelivered - the opposite of the goal -
  so it's deliberately left off. Revisiting this needs either a
  different (DTX-aware) Ogg writer or hand-rolling the Ogg container
  framing to tolerate the granule-position gaps DTX produces.
- **No silence/VAD trimming or beep tone.** Recording starts immediately
  after the greeting/tone finishes and runs for the full
  `max_recording_seconds` (or until hangup) regardless of whether the
  caller is actually speaking.
- **DTMF-triggered early hangup, "press 1 to skip greeting", etc. are not
  implemented** - matches the existing "DTMF is not relayed" blind spot in
  `receiving-calls.md`.
- **Concurrent calls are untested**, same caveat as the rest of the
  SIP/RTP path per `receiving-calls.md`.
- **The send queue is in-memory only, not persisted across restarts.** A
  voicemail recorded and enqueued but not yet sent when the process is
  stopped is lost from the queue (though its Opus/OGG file on disk is
  not - it just won't be retried automatically; resending it would need
  to be done by hand, there's no "scan `recordings_dir` for orphaned
  recordings on startup" logic). For a personal single-line deployment
  where the
  process runs continuously, this is a minor gap; it would matter more
  under frequent restarts or heavy call volume.
- **No accuracy floor on transcription.** Whisper (like any STT model)
  can mishear words, especially on noisy phone audio, and there's no
  confidence-threshold gating - a bad transcription is sent as if it
  were correct. `MaxTranscriptBytes` catches a transcript that's grown
  implausibly large (e.g. whisper.cpp's repetition-loop failure mode on
  silence or noise), but it's a size check, not an accuracy one - a
  wrong-but-plausibly-sized transcription still goes out silently.
- **GGML model files for `WhisperNetTranscriber` aren't bundled or
  auto-downloaded.** Unlike the self-contained Opus encoding path,
  `delivery = "text"` requires manually obtaining a model file and
  pointing `model_path` at it - nothing wires up
  `Whisper.net.Ggml.WhisperGgmlDownloader` to fetch one automatically.
- **CPU/memory cost on constrained hardware is unmeasured.** Whisper
  transcription is CPU-bound and model-size-dependent; how it performs
  on something like a Raspberry Pi (the README's arm64 release target)
  hasn't been measured for any model size.
- **`Whisper.net.Runtime` is a heavier dependency than the rest of this
  project's stack, and bundles every platform's native binaries
  regardless of target RID.** Unlike Concentus (pure C#), it ships
  prebuilt whisper.cpp libraries; confirmed via `dotnet publish -r
  linux-x64 --self-contained true` that the output still includes
  `runtimes/win-x64`, `runtimes/macos-arm64`, etc. alongside
  `runtimes/linux-x64` (~103 MB total for that one RID) - `dotnet
  publish -r` doesn't trim it down to just the target platform the way
  it does for packages using the standard `runtimes/{rid}/native/`
  convention. This directly bloats the linux-x64/linux-arm64 release
  artifacts built by `.github/workflows/release.yml`. Worth fixing
  (either pruning the unused `runtimes/*` folders as a post-publish
  build step, or finding whether a newer `Whisper.net.Runtime` version
  fixes the packaging) before shipping this in a release build.
