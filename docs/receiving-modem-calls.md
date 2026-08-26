# Receiving Calls on a Directly Attached Modem

This documents the second call source alongside the SIP trunk: answering an
inbound call on a phone/modem device attached directly to the machine
running sip2nostr (e.g. a USB mobile-broadband modem, or a phone used as a
modem), instead of a call arriving over a SIP trunk. Since the
hub-architecture refactor (see `docs/hub-architecture.md`), this is a
second `ICallSource` - `Modem/ModemCallSource.cs` - feeding the exact same
`CallHub`/`ICallSink` chain as `Sip/SipCallSource.cs`: it reuses
`NosCallSink`'s Nostr/WebRTC signaling, `VoicemailSink`'s answering
machine, and `LocalTestAudioSink`'s dev/testing path unchanged. Only how
the call is detected/answered and how its audio is obtained differs from
the SIP leg.

**Status: implemented, not verified against real hardware.** There is no
modem device or ModemManager instance available in this environment to
test against; e2e verification of this path is expected to be done
separately, the way `docs/receiving-calls.md` and
`docs/propagating-to-nostr.md` were verified against a real SIP trunk and a
real NosCall install. What has been verified: it builds, the ModemManager
D-Bus method/signal/property signatures match ModemManager's published
introspection XML, the pure call/config logic is unit tested, and the ALSA
P/Invoke layer is exercised end-to-end against ALSA's built-in `null` PCM
device (see `tests/Sip2Nostr.Tests/`).

## Why D-Bus, and why it doesn't cover audio

A locally attached phone/modem is controlled on Linux via
[ModemManager](https://www.freedesktop.org/software/ModemManager/), a
D-Bus-activated system service. **ModemManager's `Voice` interface only
carries call *control* (dial/ring/accept/hangup/state) - never audio.**
Call audio for a voice call on a modem is routed by the modem hardware
itself, either straight to physical audio pins (not visible to the host at
all) or, for modems with a USB audio class interface, to a PCM device the
host can open directly. This project targets the latter case, which is
common for USB mobile-broadband modules used for voice (e.g. Quectel/SIMCom
modules that enumerate a USB sound card while a call is active) - hence the
required `[modem].alsa_device` setting; there is no reliable way to
auto-detect it from ModemManager itself (see Blind Spots).

There is no existing .NET library for either ModemManager's D-Bus API or
for continuous low-latency full-duplex ALSA PCM streaming:

| Concern | Approach | Why |
|---|---|---|
| ModemManager over D-Bus | **Tmds.DBus** (0.94.x) | The established, actively maintained .NET D-Bus client (no unresolved advisories on the version used here, unlike 0.20.x). No ModemManager-specific .NET package exists, so `Modem/ModemManagerInterop.cs` defines the client-side proxy interfaces directly from ModemManager's own introspection XML (`org.freedesktop.ModemManager1.Modem.Voice.xml`, `org.freedesktop.ModemManager1.Call.xml`). |
| ALSA PCM audio | **Direct P/Invoke into `libasound.so.2`** (`Modem/AlsaPcmDevice.cs`) | The one .NET package found for ALSA (`Alsa.Net`) only exposes a file-based play-a-WAV/record-to-a-WAV API, not the continuous full-duplex raw PCM read/write a live call needs. libasound's own simplified PCM API (`snd_pcm_open`/`snd_pcm_set_params`/`snd_pcm_readi`/`snd_pcm_writei`) is small, stable, and already present on any target this project ships for (Linux x86_64/arm64, including Raspberry Pi). |
| G.711 encode/decode | **`SIPSorcery.Media.AudioEncoder`** (already a dependency) | Same class the rest of the codebase relies on for codec support; no new codec dependency needed. |

## Flow

```
 Phone/modem device (USB)
      │  voice call over the cellular/PSTN network
      ▼
 ModemManager (system D-Bus)
      │  CallAdded signal
      ▼
 ModemManagerClient            — finds the modem, watches for inbound calls
      │  OnIncomingCall(ModemIncomingCall)
      ▼
 ModemCallSource.AcceptAndRouteCallAsync
      │
      ├─ caller-list gate (same [callerlist] as the SIP leg)
      ├─ Call.AcceptAsync()          — answers the call
      ├─ wait for State → Active     — call audio isn't available before this
      ├─ open [modem].alsa_device duplex (capture + playback),
      │    wrapped in ModemCallAudio (an ICallAudio)
      │  OnIncomingCall(Call)
      ▼
 CallHub                        — routes the Call through the same sink
      │                            chain as the SIP leg, unchanged
      ├─ [nostr].enabled = true  → NosCallSink, then VoicemailSink if
      │    configured and the ring times out
      └─ [nostr].enabled = false → LocalTestAudioSink
```

`ModemCallAudio` is the only place in this leg that touches codec bytes
directly (see "Why D-Bus, and why it doesn't cover audio" above) - see
`docs/hub-architecture.md`'s "Why RTP, not PCM" section for how that fits
into the hub's design generally.

`SipCallSource` (SIP) and `ModemCallSource` (modem) run side by side as two
independent call sources attached to the same `CallHub` - enabling
`[modem]` does not change SIP behavior, and vice versa.

## Configuration

```toml
[modem]
enabled = true
alsa_device = "hw:1,0"   # or "plughw:1,0" - see Blind Spots
label = "mobile"
# modem_object_path = "/org/freedesktop/ModemManager1/Modem/0"  # optional
```

`[modem]` is entirely optional; omitting it leaves sip2nostr exactly as it
was before this feature (SIP-only), which is covered by
`ModemConfigTests.Load_ModemSectionAbsent_ModemIsNull`.

## Blind spots

- **Not verified against real hardware** (see Status above) - verify call
  detection, accept, audio in both directions, and hangup against real
  hardware before relying on this.
- **`alsa_device` is not auto-detected.** ModemManager's `Call.AudioPort`
  property exists for this, but what it names (a raw serial port needing
  vendor-specific `AT+CPCMREG`-style in-band PCM, a kernel audio device, or
  nothing at all if the modem never routes audio to the host) varies enough
  by modem model that guessing from it seemed less reliable than requiring
  it explicitly. If your modem's audio only reaches the host via a serial
  port rather than a PCM/ALSA device, this implementation does not support
  it.
- **Raw `hw:` ALSA devices only support rates/formats the hardware offers
  natively** - `snd_pcm_set_params`'s `soft_resample` flag (enabled here)
  only takes effect through ALSA's `plug` layer. If your modem's audio
  interface doesn't support 8 kHz mono S16_LE directly, use a `plughw:`
  device name (or a `.asoundrc`/`asound.conf` `plug` definition) instead of
  a raw `hw:` one so ALSA converts for you.
- **Only one modem, only one call at a time.** `ModemManagerClient` attaches
  to the first modem ModemManager reports with voice support (or the
  configured `modem_object_path`) and does not implement call waiting
  (`HoldAndAccept`) or multiparty calls - matching the SIP leg's existing
  single-call MVP scope.
- **No outbound dialing.** Like the SIP leg, this is inbound-only; there is
  no `CreateCall` support.
- **Same signaling-timeout behavior as the SIP leg**, because it's the same
  `NosCallSink` code: if the Nostr side never answers and `[voicemail]`
  isn't enabled, the modem call is left connected (accepted, but with no
  answer from `target_npub`) until the far end or the network hangs it up -
  see `docs/receiving-calls.md`'s equivalent blind spot for the SIP leg;
  nothing modem-specific about this.
- **DTMF is not relayed**, matching the SIP leg's own DTMF gap.
  `Call.SendDtmf`/`DtmfReceived` exist on the D-Bus API but aren't wired up.
