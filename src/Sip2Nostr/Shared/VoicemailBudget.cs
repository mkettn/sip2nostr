namespace Sip2Nostr.Shared;

// Shared by VoicemailSink (encodes against these), the voicemail delivery
// backends (check against these), Config/AppConfig.cs (default values for
// the configurable settings below), and ConfigLoader (validates
// max_recording_seconds against MaxRecordingSeconds - not configurable,
// derived from the NIP-17/Opus size budget - at startup). See
// docs/voicemail.md for the full derivation.
public static class VoicemailBudget
{
    public const int OpusBitrateBps = 8000;

    // Default for the configurable [voicemail].opus_resampler_quality
    // (see Config/AppConfig.cs) - the value actually passed to Concentus.
    // Oggfile.OpusOggWriteStream's own resamplerQuality parameter when
    // encoding a recording (Sinks/VoicemailSink.cs) is
    // VoicemailConfig.OpusResamplerQuality, not this constant directly.
    // Unrelated to the sample-rate conversion Sip/OggOpusCodec.Decode does.
    public const int OpusResamplerQuality = 5;

    public const int MaxAudioBytes = 30_400;

    private const int ReservedAudioBytes = MaxAudioBytes * 9 / 10;
    public const int MaxRecordingSeconds = ReservedAudioBytes / (OpusBitrateBps / 8);

    // Default for the configurable [voicemail].max_text_recording_seconds
    // (see Config/AppConfig.cs) - ConfigLoader validates against the
    // configured value, not this constant directly. Decoupled from the
    // Opus/NIP-17 budget above - text delivery's real enforcement is
    // MaxTranscriptBytes, checked against the actual transcript at send
    // time. This just bounds how much PCM VoicemailSink buffers in memory
    // while recording, regardless of what the transcript ends up being.
    // See docs/voicemail.md.
    public const int MaxTextRecordingSeconds = 600;

    // The rumor's JSON has ~40,960 bytes of padded-length budget after the
    // seal layer's own NIP-44 plaintext cap and overhead - see
    // MaxAudioBytes' derivation in docs/voicemail.md, which covers that
    // part of the math (it's the same regardless of what the rumor's
    // content actually is). A transcript isn't base64-embedded like
    // inlined audio, so - unlike MaxAudioBytes - no 4/3 base64 inflation
    // needs undoing here: this is a direct UTF-8 byte budget for the
    // transcript text itself, after rumor JSON overhead.
    //
    // 40,000 + the surrounding prose/tags in
    // TranscribedTextDeliveryBackend (~92 bytes) + rumor JSON overhead
    // (~352 bytes) leaves only ~500 bytes of headroom under the 40,960
    // bucket. Widening the prose prefix or adding tags there needs this
    // constant revisited, not just assumed to still fit.
    public const int MaxTranscriptBytes = 40_000;
}
