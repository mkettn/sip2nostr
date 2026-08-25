using SIPSorceryMedia.Abstractions;

namespace Sip2Nostr.Hub;

// A live, already-connected call handed from a source to CallHub - see
// docs/hub-architecture.md for why there's no separate "answer" step.
// LineLabel is the source's own addressable identity for this call (a
// SIP [[lines]] entry's label today); AudioFormat is the codec Audio's
// RTP frames are encoded with.
public sealed record Call(
    string CallId,
    string CallerNumber,
    string? LineLabel,
    AudioFormat AudioFormat,
    ICallAudio Audio,
    Func<Task> HangupAsync,
    Task WhenRemoteHungUp);
