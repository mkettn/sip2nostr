using SIPSorceryMedia.Abstractions;

namespace Sip2Nostr.Hub;

// A live, already-connected call handed from a source to CallHub. There is
// no separate "answer" step here - a source only ever raises OnIncomingCall
// once audio is actually flowing, so a Call is always ready to bridge or
// record immediately. LineLabel is the source's own addressable identity
// for this call (a SIP [[lines]] entry's label today); sinks that need
// per-line config (e.g. LocalTestAudioSink's test sound) look it up there.
// AudioFormat is the codec Audio's RTP frames are encoded with - a sink
// that needs actual samples (VoicemailSink, LocalTestAudioSink) decodes
// against it; a sink that only relays frames (NosCallSink) uses it to
// negotiate a matching format on the other leg.
public sealed record Call(
    string CallId,
    string CallerNumber,
    string? LineLabel,
    AudioFormat AudioFormat,
    ICallAudio Audio,
    Func<Task> HangupAsync,
    Task WhenRemoteHungUp);
