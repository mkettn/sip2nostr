# Configuring Sound Files

This covers `[[lines]].sound` (looped test audio, `LocalTestAudioSink`)
and `[voicemail].greeting_sound` (the voicemail greeting,
`VoicemailSink`) - both configured sound files, resolved the same way by
`Shared/SoundFileResolver.cs`. sip2nostr has no external-process
dependency (see issue #17): there's no `ffmpeg` or any other converter
running in the background, so a configured sound file has to already be
in one of the formats sip2nostr can read itself.

## Supported formats

| Format | Extension | How it's used |
|---|---|---|
| Raw 16-bit PCM, 8 kHz, mono | `.pcm`, `.raw`, `.s16le` | Used directly, no decoding at all |
| Opus, mono | `.opus` only | Decoded in-process (`Sip/OpusCodec.cs`), then cached as raw PCM under `/tmp/sip2nostr/sounds/` |

Nothing else is supported - not WAV, not MP3, not AAC, and **not
`.ogg`**: only a `.opus` extension is accepted for an Opus file, on
purpose (see below). This isn't a technical limit on what *could* be
decoded so much as a deliberate one: #17 removed `ffmpeg` specifically
to get rid of sip2nostr's one external-process dependency, and decoding
Opus reuses `Concentus`, already a project dependency for encoding
voicemail recordings. A codec like Vorbis is different entirely -
supporting it would mean adding a second, separate decoder library for
what's normally a couple of small, operator-authored clips you fully
control. A file that doesn't match one of the two rows above is treated
as unsupported outright, before sip2nostr ever tries to read its
contents (see "What happens if a file can't be used" below).

## Why only `.opus`, not `.ogg`

An Opus file is technically an Ogg container (that's `Sip/OpusCodec.cs`'s
own dependency, `Concentus.Oggfile`, at work) - but `.ogg` as a
*filename* extension is historically Vorbis's, not Opus's, and plenty of
tools (including many "convert to ogg" presets) still produce Ogg
Vorbis, Ogg FLAC, or other codecs when asked for a `.ogg` file. `.opus`,
by contrast, is specifically reserved for Opus files (RFC 7845).
Accepting `.ogg` at all would mean silently trying to decode files that
are very often not actually Opus - exactly the mistake that prompted
writing this doc - so sip2nostr requires `.opus` and rejects `.ogg`
outright, before even opening the file, rather than accepting `.ogg` and
hoping it's Opus inside.

That only closes off the ambiguity in the extension, not in the file's
actual contents: renaming a Vorbis (or FLAC, or anything else) file to
end in `.opus` without re-encoding it still fails - just at the decode
step instead of the extension check, since sip2nostr doesn't otherwise
inspect a file before decoding it.

**Check what's actually in a file before configuring it:**

```
$ file greeting.opus
greeting.opus: Ogg data, Opus audio, mono, 48000 Hz    # good - this works
greeting.opus: Ogg data, Vorbis audio, stereo, 44100 Hz # bad - this doesn't
greeting.opus: Ogg data, FLAC audio                     # also bad
```

(`ogginfo`/`opusinfo`, from the `vorbis-tools`/`opus-tools` packages, give
more detail if `file` isn't specific enough.)

## Converting a file to a supported format

Any of these run on your own machine, once, to *produce* the file
sip2nostr will use - they're authoring tools, not something sip2nostr
itself runs.

**To mono Opus** (recommended - much smaller than raw PCM, and what's
actually tested):

```
# via ffmpeg
$ ffmpeg -i greeting.wav -ac 1 -c:a libopus greeting.opus

# via opus-tools' opusenc (explicit about downmixing)
$ opusenc --downmix-mono greeting.wav greeting.opus
```

`-ac 1` / `--downmix-mono` forces mono - stereo Opus does decode without
error (Concentus/libopus downmixes it), but mono is the supported,
tested configuration, so don't rely on that. The source sample rate
doesn't matter: Opus encoders always resample to one of the codec's own
rates internally, and `OpusCodec.Decode` can decode at whatever rate the
caller needs (8 kHz for playback here) independent of what the file was
encoded at, so there's no `-ar` to get right.

**To raw PCM** (larger file, but skips the Opus format question entirely
- useful if you just want something that's guaranteed to work):

```
$ ffmpeg -i greeting.wav -ac 1 -ar 8000 -f s16le greeting.pcm
```

## What happens if a file can't be used

A configured (non-empty) `[[lines]].sound` or `[voicemail].greeting_sound`
that doesn't exist, isn't `.pcm`/`.raw`/`.s16le`/`.opus`, or fails to
decode (wrong codec, corrupt, etc.) is a **startup error**: `ConfigLoader`
eagerly resolves every configured sound file via the same
`SoundFileResolver` the sinks use, and refuses to start if any of them come
back unusable. The failure reason - missing file, unsupported
extension, decode failure - is embedded directly in that startup error, not
just logged separately: `ConfigLoader` runs before the "real" (run-file)
logger exists, so a bare log line here would only ever reach the console.
The possible reasons:

- `Configured sound file {path} resolved to {resolvedPath}, but it does
  not exist.` - path/typo problem.
- `Configured sound file {path} is not a supported format; only raw 8
  kHz 16-bit PCM (.pcm/.raw/.s16le) and Opus (.opus) files are
  supported.` - wrong extension (a `.ogg` file included - see above).
- `Could not decode {path}; provide an Opus file or raw 8 kHz
  16-bit PCM.` - `.opus` extension, but the file isn't actually a
  decodable Opus stream (see "Why only `.opus`, not `.ogg`" above) or is
  genuinely corrupt.

Leaving `sound`/`greeting_sound` unset entirely is fine - that's the
"no sound file configured, play a tone instead" case (a looping sine wave
for `[[lines]].sound`, a short tone before recording for
`[voicemail].greeting_sound`), unrelated to the fail-fast check above,
which only ever fires for a value that's actually set but broken.

This check only runs for the sink that would actually play the file:
`[[lines]].sound` only when `[nostr].enabled = false` (`LocalTestAudioSink`
is the only reader), `[voicemail].greeting_sound` only when `[nostr]` *and*
`[voicemail]` are both enabled (`VoicemailSink`'s the only reader) - see
`Program.cs`'s sink wiring. A broken `sound`/`greeting_sound` left over
from switching modes doesn't block startup in a mode where it's never
read.

## Config reference

```toml
[[lines]]
uri = "sip:+4989123456@sip.your-provider.de"
label = "main"
sound = "sounds/test.opus"          # relative to the config file unless rooted

[voicemail]
greeting_sound = "sounds/greeting.opus"
```

See `config.example.toml` for the full annotated example.
