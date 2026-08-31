# Configuring Sound Files

This covers `[[lines]].sound` (looped test audio, `LocalTestAudioSink`)
and `[voicemail].greeting_sound` (the voicemail greeting,
`VoicemailSink`) - both configured sound files, resolved the same way by
`Shared/SoundFileResolver.cs`. sip2nostr has no external-process
dependency (see issue #17): there's no `ffmpeg` or any other converter
running in the background, so a configured sound file has to already be
in one of the formats sip2nostr can read itself. **Not every file with a
`.ogg` extension qualifies** - see "The most common mistake" below.

## Supported formats

| Format | Extension | How it's used |
|---|---|---|
| Raw 16-bit PCM, 8 kHz, mono | `.pcm`, `.raw`, `.s16le` | Used directly, no decoding at all |
| Ogg Opus, mono | `.ogg`, `.opus` | Decoded in-process (`Sip/OggOpusCodec.cs`), then cached as raw PCM under `/tmp/sip2nostr/sounds/` |

Nothing else is supported - not WAV, not MP3, not AAC, and critically
**not Ogg Vorbis** (see below). If a configured file doesn't match one of
the two rows above, it's treated as a decode failure (see "What happens
if a file can't be used" below).

## The most common mistake: `.ogg` doesn't mean Opus

`.ogg` is a *container* extension, not a codec name. Historically it's
associated with Vorbis, and plenty of tools (including many "convert to
ogg" presets) still produce Ogg Vorbis, Ogg FLAC, or other codecs inside
an `.ogg` file - none of which sip2nostr can decode. `Sip/OggOpusCodec.cs`
only understands Opus-in-Ogg (it's built on Concentus, an Opus-only
library), and a `.ogg` file with a different codec inside it is not a
recoverable situation the way a wrong sample rate or channel count would
be - it fails to decode outright.

**Check what's actually in the file before configuring it:**

```
$ file greeting.ogg
greeting.ogg: Ogg data, Opus audio, mono, 48000 Hz    # good - this works
greeting.ogg: Ogg data, Vorbis audio, stereo, 44100 Hz # bad - this doesn't
greeting.ogg: Ogg data, FLAC audio                     # also bad
```

(`ogginfo`/`opusinfo`, from the `vorbis-tools`/`opus-tools` packages, give
more detail if `file` isn't specific enough.)

## Converting a file to a supported format

Any of these run on your own machine, once, to *produce* the file
sip2nostr will use - they're authoring tools, not something sip2nostr
itself runs.

**To mono Ogg Opus** (recommended - much smaller than raw PCM, and what's
actually tested):

```
# via ffmpeg
$ ffmpeg -i greeting.wav -ac 1 -c:a libopus greeting.ogg

# via opus-tools' opusenc (explicit about downmixing)
$ opusenc --downmix-mono greeting.wav greeting.ogg
```

`-ac 1` / `--downmix-mono` forces mono - stereo Opus does decode without
error (Concentus/libopus downmixes it), but mono is the supported,
tested configuration, so don't rely on that. The source sample rate
doesn't matter: Opus encoders always resample to one of the codec's own
rates internally, and `OggOpusCodec.Decode` can decode at whatever rate
the caller needs (8 kHz for playback here) independent of what the file
was encoded at, so there's no `-ar` to get right.

**To raw PCM** (larger file, but skips the Ogg/Opus format question
entirely - useful if you just want something that's guaranteed to work):

```
$ ffmpeg -i greeting.wav -ac 1 -ar 8000 -f s16le greeting.pcm
```

## What happens if a file can't be used

A file that doesn't exist, isn't `.pcm`/`.raw`/`.s16le`/`.ogg`/`.opus`, or
fails to decode (wrong codec, corrupt, etc.) is **not a startup error** -
`SoundFileResolver.Resolve` logs a `Warning` and returns `null`, and the
caller falls back to a short sine-wave tone instead (see #26 on making
this fail fast at config load instead). Concretely:

- `[voicemail].greeting_sound` unset *or* unusable → the same ~1.5s tone
  plays before recording starts either way; there's no way to tell from
  behavior alone which case you're in.
- `[[lines]].sound` unset *or* unusable → a looping sine wave test tone
  plays instead, logged as `Configured sound file {path} for line
  {label} could not be used; sending sine wave instead.`

**So: if a configured greeting/test sound isn't playing, check the logs
at `Warning` level**, not just for errors - a bad file degrades silently
into the fallback tone rather than crashing or refusing to start. Look
for one of:

- `Configured sound file {path} resolved to {resolvedPath}, but it does
  not exist.` - path/typo problem.
- `Configured sound file {path} is not a supported format; only raw 8
  kHz 16-bit PCM (.pcm/.raw/.s16le) and mono Ogg/Opus (.ogg/.opus) files
  are supported.` - wrong extension.
- `Could not decode {path}; provide a mono Ogg/Opus file or raw 8 kHz
  16-bit PCM.` - right extension, wrong codec inside it (see "The most
  common mistake" above) or a genuinely corrupt file.

## Config reference

```toml
[[lines]]
uri = "sip:+4989123456@sip.your-provider.de"
label = "main"
sound = "sounds/test.ogg"          # relative to the config file unless rooted

[voicemail]
greeting_sound = "sounds/greeting.ogg"
```

See `config.example.toml` for the full annotated example.
