using SIPSorceryMedia.Abstractions;

namespace Sip2Nostr.Hub;

// A ringing call handed from a source to CallHub - see
// docs/hub-architecture.md for why answering is a sink's decision. Until
// a sink calls AnswerAsync the caller hears ringback and no audio flows,
// so a sink has to answer before using Audio. AnswerAsync answers at most
// once per call - every sink offered the call sees the same outcome - and
// returns false if the call can no longer be answered (the caller gave
// up, or the source couldn't answer it). LineLabel is the source's own
// addressable identity for this call (a SIP [[lines]] entry's label
// today); AudioFormat is the codec Audio's RTP frames are encoded with.
public sealed record Call(
    string CallId,
    string CallerNumber,
    string? LineLabel,
    AudioFormat AudioFormat,
    ICallAudio Audio,
    Func<Task<bool>> AnswerAsync,
    Func<Task> HangupAsync,
    Task WhenRemoteHungUp);
