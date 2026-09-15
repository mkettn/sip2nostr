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

Verified end-to-end against a real SIP trunk and a real NIP-17 client for
`delivery = "text"`: a call that falls back to voicemail plays the
greeting/tone, records the caller, saves the recording as Opus,
transcribes it, and delivers the transcript as DM text. `delivery =
"audio"` (the default) is new and, per the Blind spots below, not yet
verified against a real Blossom server or a real NIP-17 client's
handling of a kind 15 file message - only exercised against a local fake
server in `tests/Sip2Nostr.Tests/AudioDeliveryBackendTests.cs`. The
`MissedCallNoticeJob` path (recording too short / caller hangs up before
anything is captured) and the local-only fallback (see Delivery backends
below) haven't specifically been exercised against a real call either,
though both share code paths with the verified `VoicemailAudioJob`/kind
14 send path.

## Flow

Since the hub-architecture refactor (see `docs/hub-architecture.md`), the
ring-timeout race lives in `NosCallSink` and the recording flow lives in
`VoicemailSink` - two `ICallSink`s tried in order by `CallHub` for the same
`Call`, rather than one function doing both. The call is still ringing
when `VoicemailSink` gets it: `NosCallSink` only answers on a real Nostr
answer, so a caller who ends up at voicemail hears ringback right up to
the moment the greeting starts.

```
 CallHub, routing a Call from SipCallSource (Nostr enabled)
      │
      ├─ SIP call ringing; nothing has answered it yet
      │
      ▼
 NosCallSink.TryHandleAsync
      ├─ WebRTC offer sent over Nostr, same as today
      │
      ▼
 Wait for: Nostr answer | callee declines/hangs up | ring_timeout_seconds
           elapses | caller hangs up | signaling fails
      │
      ├─ Nostr answers in time  → bridge audio, returns true (handled)
      ├─ Caller hangs up first  → tear down, returns true (handled)
      │
      └─ Callee declined, timeout, or signaling failure → decline (return
         false); CallHub
         offers the Call to the next configured sink, VoicemailSink:
           1. Close the WebRTC peer connection; the SIP leg's Call.Audio
              is reused directly - no new media session is created.
           2. Send a NIP-AC `hangup` over Nostr so a ringing device (e.g.
              NosCall) stops ringing (best-effort; failure is logged, not
              fatal) - the bridge originated this call, so giving up on it
              is a hangup, not a reject (the callee's decline signal).
              Skipped when the callee is the one who ended it: a device
              that just hung up doesn't need telling to stop ringing.
           3. Answer the SIP leg (`Call.AnswerAsync`) - this is where the
              caller stops hearing ringback. If it returns false the
              caller gave up while it was ringing: log it and enqueue a
              MissedCallNoticeJob, same as a hangup during the greeting.
           4. Play `greeting_sound` once (or a short tone if unset). If the
              caller hangs up here, log it (caller number included) and
              enqueue a MissedCallNoticeJob on VoicemailSender - nothing
              was recorded, but it's still a missed call.
           5. Record caller audio for up to `max_recording_seconds`, or
              until they hang up.
           6. Encode the recording to Opus (in-process via
              `Sip/OpusCodec.cs`) and save it under `recordings_dir`,
              named per `recording_filename` (always - this is the
              durability point, independent of whatever happens to the
              send afterward).
           7. Return true (handled) - CallHub's own `finally` calls
              `Call.HangupAsync` unconditionally once a sink is done, so
              VoicemailSink doesn't need to hang up the SIP call itself.
              This always runs even if step 6 threw (e.g. a bad
              `greeting_sound` path, a disk error), so a failure there
              can't leave the caller on a silent, still-connected call or
              leak the RTP session.
           8. If the recording is long enough to be worth sending, enqueue
              a VoicemailAudioJob (the Opus path) on VoicemailSender
              and move on - delivery happens off this call's critical
              path, in a separate background worker. Otherwise (too
              short), log it and enqueue a MissedCallNoticeJob instead -
              the same "exactly one job" rule as step 4.
           9. If RunVoicemailAsync throws instead of reaching step 8 (a
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
         from the already-Opus-encoded recording on disk (encoded at
         record time by VoicemailSink, not here - see Delivery backends
         below). Sent as a NIP-17 kind 14 private message for every
         backend except the "audio" backend's Blossom upload, which
         sends a kind 15 file message instead (the backend's result says
         which - see Delivery backends). A relay rejecting the event
         (e.g. too large) is detected and logged as a failure, not
         reported as sent.
      │
      ▼
 Disconnect, go back to idle
```

## Delivery backends

How a recorded voicemail becomes DM content is pluggable via
`[voicemail].delivery`, which is either `"audio"` or `"text"` - each
mode has a requirement, and if it isn't configured, sip2nostr degrades
to a local-only notice rather than failing to start or trying to inline
the recording:

- `"audio"` (default) - `Voicemail/AudioDeliveryBackend.cs` reads the
  recording (already Opus - `VoicemailSink` encodes it when saving, not
  this backend), AES-256-GCM encrypts it with a freshly generated key and
  nonce, and uploads only the ciphertext (the server never sees the
  plaintext, or the bridge's actual Nostr key beyond a signed auth event)
  to a [Blossom](https://github.com/hzrd149/blossom) (BUD-01/BUD-02)
  server from `[voicemail.blossom].servers` - tried in order until one
  accepts it. It then sends a NIP-17 **kind 15** file message: `content`
  is the uploaded URL, tags carry `decryption-key`/`decryption-nonce`
  (hex-encoded), `encryption-algorithm` (`aes-gcm`), `x`/`ox` (sha256 of
  the encrypted/original bytes), and `size`. Authentication is a signed,
  10-minute-lived kind 24242 event (BUD-02's `t: upload` + `x: <sha256 of
  the exact blob>`), built and signed with `[nostr].bridge_nsec` directly
  - no separate identity or credential for the storage server. Because
  the DM itself only ever carries a URL, this mode has no
  recording-length cap beyond the shared `max_recording_seconds` sanity
  limit below; if every configured server rejects the upload, the job
  fails (the recording stays on disk, same as any other delivery
  failure). Requires at least one entry in `[voicemail.blossom].servers`.
- `"text"` - `Voicemail/TranscribedTextDeliveryBackend.cs` transcribes
  `VoicemailAudioJob.Samples` - the original recorded PCM, carried on the
  job alongside `OpusPath` rather than decoded back out of the saved
  Opus file - via a speech-to-text engine, sending the transcript as
  plain text instead. Transcribing the original PCM instead of a
  lossy-recompressed copy avoids feeding whisper.cpp audio that's already
  been through 8 kbps Opus once. The transcript is checked against
  `VoicemailBudget.MaxTranscriptBytes` (40,000 bytes) at send time - a
  transcript is normally tiny compared to that, but whisper.cpp can fall
  into a repetition loop on silence or noise and produce far more text
  than any real voicemail would, so the check guards against that rather
  than being trusted to never trigger. If nothing could be transcribed
  (silence, an engine failure), the backend falls back to
  `[voicemail.blossom]` (the same encrypted upload `"audio"` uses) when
  it's configured, or a local-only notice otherwise; a failure in the
  fallback itself falls through to the notice too, so a transcription
  failure never ends up with nothing sent at all. Requires
  `[voicemail.transcription].model_path`.

If the active mode's requirement isn't configured, `Program.cs` logs a
startup warning and uses `Voicemail/LocalOnlyDeliveryBackend.cs` instead:
a plain-text notice naming the caller and pointing at `recordings_dir`,
where the recording is already saved regardless (`VoicemailSink` writes
it unconditionally, before any delivery backend ever runs) - the operator
is expected to fetch and play it by hand. Recording length is no longer bound by what fits inside a single NIP-17
message the way it would be if the recording were inlined as a base64
`data:` URI directly in the DM content - that approach isn't attempted at
all here, configured or not; both delivery modes work by reference (a
transcript, or an uploaded file's URL) instead.

`AudioDeliveryBackend` sends a kind 15 file message; the other two
(`TranscribedTextDeliveryBackend`, `LocalOnlyDeliveryBackend`) send a
kind 14 private message. All three implement
`Voicemail/IVoicemailDeliveryBackend.cs` (`BuildContentAsync(VoicemailAudioJob,
CancellationToken) -> (Content, Tags, Description, Kind)`, plus a
`RequiresPcm` property - `false` for `AudioDeliveryBackend`/
`LocalOnlyDeliveryBackend`, `true` for `TranscribedTextDeliveryBackend` -
that `VoicemailSink` reads to decide whether to populate
`VoicemailAudioJob.Samples`, so that decision lives with the backend
that actually knows its own needs rather than being re-derived from
`[voicemail].delivery` at the recording site). `Kind` (`PrivateMessage`
or `FileMessage`) is part of each call's result rather than a fixed
per-backend property, because `TranscribedTextDeliveryBackend` returns
either one depending on whether a given call ends up delegating to its
Blossom fallback. `VoicemailSender` reads `Kind` to decide whether to
call `Client.SendPrivateMsgTo` (kind 14) or build and gift-wrap a kind 15
rumor itself (`Client.GiftWrapTo`) - the only thing it depends on the
delivery backend for; it doesn't otherwise know or care which backend
it's holding, and owns disposing it alongside its own worker. `Program.cs`
passes the same backend instance's `RequiresPcm` to `VoicemailSink`
separately, since the sink itself only calls `VoicemailSender.Enqueue`
and never touches the backend directly.

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
    against `Task.Delay(ringTimeoutSeconds)`, `Call.WhenRemoteHungUp`, and
    `NostrSignalingClient.WhenCalleeHungUp` - a callee who declines or
    hangs up while their device is ringing gets to voicemail immediately
    instead of waiting out a timeout that, with `ringTimeoutSeconds`
    unset, would never come (see `docs/propagating-to-nostr.md`).
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
  - Answers the call itself (`Call.AnswerAsync`, see
    `docs/hub-architecture.md`) right before playing the greeting, so the
    caller rings rather than sitting on a silent connected call for
    `ring_timeout_seconds` first, then reuses that **same `Call.Audio`**
    directly instead of building a second media session: a `SIPSorcery.Media.AudioExtrasSource`
    plays the greeting/tone, wired via `OnAudioSourceEncodedSample +=
    call.Audio.SendEncodedSample` instead of the pre-hub code's `+=
    sipMediaSession.SendAudio` - same off-the-shelf player, same signature
    (`ICallAudio.SendEncodedSample` exists specifically to match it), just
    retargeted at the hub's `ICallAudio` instead of a concrete
    `RTPSession`. `AudioExtrasSource` needs a real file to stream from,
    so the sound-file path from
    `SoundFileResolver.Resolve` is opened directly (`File.OpenRead` +
    `SendAudioFromStream`) rather than loaded into a `short[]` first, and
    the no-greeting-configured case now uses `SetSource(SineWave)` instead
    of a hand-generated tone. Meanwhile an `OnAudioReceived` subscriber
    decodes each inbound frame via
    `SIPSorcery.Media.AudioEncoder.DecodeAudio` and buffers the PCM - the
    hub itself only ever hands over RTP frames, so this sink is the one
    that knows how to turn them into samples.
  - `SaveRecordingAsync` encodes the buffered PCM samples to Opus via
    `Sip/OpusCodec.Encode` (pure logic, unit tested; also used to
    decode `[[lines]].sound`/`[voicemail].greeting_sound` files in
    `Shared/SoundFileResolver.cs` - see `docs/sound-files.md` for the
    format it requires and how a misconfigured file degrades) before
    writing the result under `recordings_dir` - so the file on disk is
    already exactly what `delivery = "audio"` encrypts and uploads, with
    no separate re-encode step at delivery time. `OpusCodec.Encode` runs
    with `UseVBR = false`: Concentus (like libopus) defaults to VBR,
    where `Bitrate` is only a target the encoder can exceed on complex
    input - CBR keeps a recording's encoded size roughly proportional to
    its duration, useful for anyone budgeting `recordings_dir` disk
    space. Its `OpusOggWriteStream`
    deliberately has no `using` - it isn't `IDisposable`; `Finish()` is
    what pads the trailing frame, writes the end-of-stream page, and
    flushes, and `leaveOpen` keeps the underlying `MemoryStream` readable
    afterwards. `OpusCodec.Encode`'s `resamplerQuality` argument is
    `[voicemail].opus_resampler_quality` (default 5, `VoicemailBudget.
    OpusResamplerQuality` - Concentus rejects anything outside 0-10, and
    `ConfigLoader` checks that at startup too); `OpusOggWriteStream` only
    consults it to build a resampler for when its own input and encoder
    sample rates differ, which they never do here (`Encode` always passes
    the same `sampleRate` for both), so today this setting has no
    observable effect - it's exposed anyway per #21, for whenever that
    changes.
  - `ResolveRecordingFilename` builds the saved file's name from
    `[voicemail].recording_filename` (default `{timestamp}-{caller}.opus`)
    by substituting `{timestamp}` (`yyyyMMdd-HHmmss`, matching
    `[logging].run_file`'s own templating convention in `Program.cs`),
    `{caller}` (`Call.CallerNumber`, already normalized to digits by
    `PhoneNumberNormalizer` - see `caller-allowlist.md` - so it's always
    a filesystem-safe path segment even though it's attacker-controlled
    From-header data), and `{call_id}` (`Call.CallId`, a `Guid` - not a
    phone number, but useful if you'd rather the caller's number not
    appear in filenames). A template that includes neither `{timestamp}`
    nor `{call_id}` would let concurrent calls silently overwrite each
    other's recording, and a rooted template or one containing a `..`
    segment could write outside `recordings_dir` entirely, so
    `Config/ConfigLoader.cs` rejects all three at startup (see below).
    `SaveRecordingAsync` resolves the full path (`recordings_dir` + the
    templated filename) before creating its directory, so a template
    with a path separator in it (e.g. `{caller}/{timestamp}.opus`, to
    group recordings per caller) still works - it's only a rooted path
    or a `..` segment that's rejected, not subdirectories in general.
  - `TryHandleAsync` always returns `true`: `CallHub`'s own `finally`
    calls `Call.HangupAsync` unconditionally once a sink is done, so this
    sink doesn't hang up the SIP call itself and doesn't need a `finally`
    of its own to guarantee that happens even if `RunVoicemailAsync`
    throws. `RunVoicemailAsync` only ever writes the Opus recording
    and calls `VoicemailSender.Enqueue` - it has no Nostr.Sdk dependency
    at all, so nothing in the call-handling path blocks on relay
    connectivity or a publish.
  - `[voicemail].ring_timeout_seconds` and `max_recording_seconds` are
    validated (`> 0`) in `Config/ConfigLoader.cs` at startup, alongside
    the rest of config loading - an unchecked bad value would otherwise
    surface deep inside `Task.Delay` as every call being silently routed
    to voicemail with a misleading "signaling failed" log line.
    `max_recording_seconds` is also rejected there if it exceeds
    `Shared/VoicemailBudget.cs`'s `MaxRecordingSecondsCeiling` (3600s) -
    a single ceiling regardless of `delivery`, since neither mode inlines
    the recording in the DM: it's purely a sanity limit on how much PCM
    `VoicemailSink` buffers in memory while recording, not a size budget,
    so a value that would buffer more than intended fails at startup
    rather than only after a caller has already left a message. Whether
    the configured `delivery` mode's own requirement
    (`[voicemail.transcription].model_path` for `"text"`,
    `[voicemail.blossom].servers` for `"audio"`) is actually configured
    is deliberately *not* checked here - `Program.cs` handles that with a
    warning and `LocalOnlyDeliveryBackend`, not a startup failure, since
    leaving a mode's requirement unset is a valid choice, not a mistake
    (see Delivery backends above). `[voicemail].opus_resampler_quality` (default
    `VoicemailBudget.OpusResamplerQuality`, 5) is validated against the
    `0`-`10` range Concentus itself enforces (see the `VoicemailSink.cs`
    bullet above) - checking it here means a bad value fails at startup,
    not on the first voicemail encoded. `[voicemail].recording_filename`
    is validated as: non-empty; containing `{timestamp}` or `{call_id}`
    (case-insensitively); not rooted; and containing no `..` path
    segment - the latter two because `SaveRecordingAsync` joins the
    resolved filename straight onto `recordings_dir`, and a rooted value
    would silently discard `recordings_dir` entirely (`Path.Combine`'s
    documented behavior) while a `..` segment could escape it, so a
    template can only ever name something under `recordings_dir`.
- `Voicemail/VoicemailSender.cs`: one instance, constructed once in
  `Program.cs` and shared across every call for the life of the process -
  unlike `NostrSignalingClient`, which is scoped to a single call.
  - `Enqueue` takes a `Voicemail/SendJob.cs` - either a `MissedCallNoticeJob`
    (`CallerNumber`, `CallId`, no audio) or a `VoicemailAudioJob` (adds
    `OpusPath`, `Samples`, `SampleRate`, `DurationSeconds` - both the saved
    Opus file and the original recorded PCM, so each delivery backend
    reads whichever it actually needs; `VoicemailSink` only populates
    `Samples` when the configured backend's `IVoicemailDeliveryBackend.RequiresPcm`
    says so (`true` for `TranscribedTextDeliveryBackend`, `false` for
    `AudioDeliveryBackend`/`LocalOnlyDeliveryBackend`, neither of which
    reads it and would otherwise carry the full recording in memory for
    every "audio" job unread) - and writes it to an unbounded `System.Threading.Channels.Channel<SendJob>`, returning
    immediately. It's a plain in-memory queue (multiple calls can enqueue
    concurrently - `Channel` is built for that), not a persistent one, so
    anything still queued at process shutdown is logged as undelivered;
    a `VoicemailAudioJob`'s Opus recording is safely already on disk
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
    `Enqueue` - though a `VoicemailAudioJob`'s Opus recording stays
    safe on disk regardless; a `MissedCallNoticeJob` has nothing else
    backing it up.
  - `BuildMissedCallNoticeContent` builds a short plain-text DM naming
    `CallerNumber`; no encoding, no size check needed - it's well under
    any NIP-17 budget.
  - Per `VoicemailAudioJob`: delegates to the configured
    `IVoicemailDeliveryBackend` (see Delivery backends above) rather than
    encoding anything itself - the recording is already Opus on disk
    by the time `VoicemailSender` ever sees the job (`Sinks/VoicemailSink.cs`
    encodes it at record time; deliberately not `ffmpeg`/any external
    process, keeping the whole feature working in a self-contained
    single-file binary with nothing to install on the host). The
    backend's result `Kind` decides how it's sent: `PrivateMessage`
    (`TranscribedTextDeliveryBackend`, `LocalOnlyDeliveryBackend`) calls
    `Client.SendPrivateMsgTo(relayUrls, ...)` - NIP-17: rumor, seal, gift
    wrap, and publish all handled by `Nostr.Sdk`; `FileMessage`
    (`AudioDeliveryBackend`) instead builds a kind 15 `EventBuilder`
    rumor from the backend's `Content`/`Tags` directly and gift-wraps it
    itself (`Client.GiftWrapTo`), since `SendPrivateMsgTo` only ever
    builds a kind 14 rumor. Either way the target is exactly the relay
    set (`[voicemail].dm_relays`, or `[nostr].relays` as a fallback) this
    batch just connected to - `SendPrivateMsgTo`/`GiftWrapTo`'s `...To`
    overloads are used explicitly rather than the plain ones so the
    relays published to are always the same ones just verified reachable,
    rather than Nostr.Sdk's own separate NIP-17 relay-discovery landing
    on a different set. Unlike the NIP-AC signaling wrap `NostrSignalingClient`
    uses for call offer/answer/ICE, a voicemail should be readable by any
    NIP-17-capable client, not just NosCall. The returned
    `SendEventOutput` is checked (shared `PublishOutcome.ThrowIfFailed`
    helper, also used by `NostrSignalingClient.PublishAsync`) - a relay
    can reject an event with `OK: false` without the publish call itself
    throwing, so this is the only way to actually detect it. A failure
    for one job in a batch is caught and logged per-job (the Opus
    path is included for a `VoicemailAudioJob`); it doesn't stop the rest
    of the batch from being attempted.

## Blind spots

- **A delivery backend that throws drops the job silently instead of
  falling back to a notice.** `VoicemailSender.PrepareJobSafeAsync`
  catches any exception from `BuildContentAsync`, logs it, and returns
  `null` - the job is simply never sent, not even as a plain-text notice.
  This applies to `AudioDeliveryBackend` when every configured Blossom
  server rejects the upload, and to `TranscribedTextDeliveryBackend` when
  transcription *succeeds* but the transcript itself exceeds
  `MaxTranscriptBytes` (the transcription-*fails* path is already fully
  handled - see Delivery backends above - and never throws this far).
  Either way the recording is still safe on
  disk (logged in the error), but `target_npub` gets nothing over Nostr
  for that call at all - the "exactly one DM per missed call, never
  neither" invariant stated at the top of this document doesn't actually
  hold for this specific failure path.
- **No fallback if Opus encoding fails when a recording is saved.**
  The recording is encoded to Opus at record time
  (`VoicemailSink.SaveRecordingAsync`, via `Sip/OpusCodec.Encode`), so
  an encoding failure there means nothing is saved to disk at all, not
  just undelivered - `RunVoicemailAsync` throws, and
  `VoicemailSink.TryHandleAsync`'s own catch sends a `MissedCallNoticeJob`
  instead (see Flow step 8). A genuine encoding failure is not expected
  in normal operation (no I/O, no external process - see DTX below for
  the one known throw path, which is deliberately never triggered), but
  when it happens it costs the recording itself, not just the send.
- **DTX (encoder silence-dropping) is not available, so the size problem
  above can't currently be helped by compressing the silence out of a
  recording.** `Concentus.Oggfile`'s `OpusOggWriteStream` - the
  container writer `Sip/OpusCodec.Encode` uses - unconditionally
  rejects a DTX-enabled encoder at construction (`ArgumentException("DTX
  is not currently supported in Ogg streams")`, confirmed by reading its
  source). Enabling `IOpusEncoder.UseDTX` would make every encode throw,
  and with no fallback if that happens (see above) that means the
  recording is lost, not just undelivered - the opposite of the goal -
  so it's deliberately left off. Revisiting this needs either a
  different (DTX-aware) container writer or hand-rolling the container
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
  stopped is lost from the queue (though its Opus file on disk is
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
- **`delivery = "audio"` (the default) hasn't been verified against a
  real Blossom server or a real NIP-17 client.** This is the highest-
  impact gap in this document, since it's what a default install now
  actually depends on. `AudioDeliveryBackendTests.cs` exercises the
  encryption, BUD-02 auth event, and upload request/response handling
  against a local fake HTTP server - real protocol-level details (a
  specific server's exact error responses, whether Amethyst or another
  client actually renders a kind 15 `audio/ogg` attachment the way this
  implementation expects) are unverified.
- **No BUD-06 `HEAD /upload` preflight.** A client MAY ask a server
  whether it would accept an upload (size, content type) before sending
  the bytes; `AudioDeliveryBackend` always goes straight to `PUT`.
  Skipping it is spec-compliant and costs nothing extra for a voicemail
  (small, and there's no bandwidth pressure this bridge needs to
  conserve), but means a server-side rejection is only discovered after
  the full upload already happened.
- **The BUD-02 auth event's 10-minute expiration is fixed, not
  configurable.** Long enough for a normal upload to a responsive
  server; not adjustable if a particular server or network needs more
  (or less) headroom.
- **No blob retention/expiration is requested from the server.** Some
  Blossom servers accept an `expiration` tag on the auth event as a hint
  for how long to keep the blob, but `AudioDeliveryBackend`
  doesn't send one (its `expiration` tag is the auth event's own replay
  window, not a retention request) - how long a voicemail stays
  reachable at its uploaded URL is entirely up to the server's own
  policy. This was an open question in issue #15 and is still open here.
