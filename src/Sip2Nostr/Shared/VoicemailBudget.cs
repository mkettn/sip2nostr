namespace Sip2Nostr.Shared;

// Shared by VoicemailSink (encodes against OpusBitrateBps/OpusResamplerQuality),
// the voicemail delivery backends (check against MaxTranscriptBytes),
// Config/AppConfig.cs (default values for the configurable settings
// below), and ConfigLoader (validates max_recording_seconds against
// MaxRecordingSecondsCeiling at startup). See docs/voicemail.md.
public static class VoicemailBudget
{
    public const int OpusBitrateBps = 8000;

    // Default for the configurable [voicemail].opus_resampler_quality
    // (see Config/AppConfig.cs) - the value actually passed to
    // Concentus.Oggfile.OpusOggWriteStream's own resamplerQuality
    // parameter when encoding a recording (Sinks/VoicemailSink.cs) is
    // VoicemailConfig.OpusResamplerQuality, not this constant directly.
    // Unrelated to the sample-rate conversion Sip/OpusCodec.Decode does.
    public const int OpusResamplerQuality = 5;

    // Default for the configurable [voicemail].max_recording_seconds (see
    // Config/AppConfig.cs). Not derived from anything - neither delivery
    // mode inlines the recording in the DM itself ("text" sends a
    // transcript, "audio" sends a Blossom upload's URL - see
    // docs/voicemail.md), so this is just a sanity limit on how much PCM
    // VoicemailSink buffers in memory while recording, not a size budget.
    public const int MaxRecordingSeconds = 600;

    // Hard ceiling ConfigLoader enforces on the configurable
    // max_recording_seconds, so raising that sanity limit can't itself
    // become unbounded. Picked as an order-of-magnitude memory budget,
    // not a precise derivation: at 8 kHz mono 16-bit PCM (16,000
    // bytes/sec), 3,600s of buffered audio is ~57.6 MB in
    // VoicemailSink's List<short> alone, before List growth/ToArray()
    // transients or VoicemailAudioJob.Samples keeping a copy alive in
    // the send queue - comfortably bounded even accounting for those,
    // but well past any real voicemail's length.
    public const int MaxRecordingSecondsCeiling = 3600;

    // The rumor's JSON has ~40,960 bytes of padded-length budget after
    // NIP-44's own plaintext cap and the seal layer's overhead - see
    // docs/voicemail.md for the full derivation. A transcript isn't
    // base64-embedded the way inlined audio used to be, so this is a
    // direct UTF-8 byte budget for the transcript text itself, after
    // rumor JSON overhead.
    //
    // 40,000 + the surrounding prose/tags in
    // TranscribedTextDeliveryBackend (~92 bytes) + rumor JSON overhead
    // (~352 bytes) leaves only ~500 bytes of headroom under the 40,960
    // bucket. Widening the prose prefix or adding tags there needs this
    // constant revisited, not just assumed to still fit.
    public const int MaxTranscriptBytes = 40_000;
}
