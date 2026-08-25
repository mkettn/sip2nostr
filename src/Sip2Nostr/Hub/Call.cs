namespace Sip2Nostr.Hub;

// A live, already-connected call handed from a source to CallHub. There is
// no separate "answer" step here - a source only ever raises OnIncomingCall
// once audio is actually flowing, so a Call is always ready to bridge or
// record immediately. LineLabel is the source's own addressable identity
// for this call (a SIP [[lines]] entry's label today); sinks that need
// per-line config (e.g. LocalTestAudioSink's test sound) look it up there.
public sealed record Call(
    string CallId,
    string CallerNumber,
    string? LineLabel,
    ICallAudio Audio,
    Func<Task> HangupAsync,
    Task WhenRemoteHungUp);
