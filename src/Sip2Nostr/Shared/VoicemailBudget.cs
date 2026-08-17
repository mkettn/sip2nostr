namespace Sip2Nostr.Shared;

// The Opus bitrate VoicemailSender encodes at, and the resulting hard
// ceiling on recording length that still fits in a NIP-17 voicemail DM.
// Shared between VoicemailSender (which encodes at OpusBitrateBps and
// enforces MaxAudioBytes against the actual encoded output) and
// ConfigLoader (which validates [voicemail].max_recording_seconds
// against MaxRecordingSeconds at startup) so a misconfiguration that can
// never be delivered fails fast instead of only surfacing after a caller
// has already left an undeliverable message. Namespaced separately from
// both Config and Voicemail (rather than living in either) since both of
// those already depend on this file, and it doesn't depend on either of
// them back.
public static class VoicemailBudget
{
    // 8kbps: the recording source is 8kHz telephony-bandwidth PCM
    // (G.711), so a higher bitrate spends bits on frequency content that
    // was never there in the first place - halving the original 16kbps
    // default costs no audible quality at this bandwidth while roughly
    // doubling how much recording time fits under MaxAudioBytes below.
    public const int OpusBitrateBps = 8000;

    // The largest Opus-encoded (pre-base64) audio payload that reliably
    // fits in a NIP-17 voicemail DM - this is the hard ceiling
    // VoicemailSender checks the actual encoded output against before
    // sending, independent of whatever MaxRecordingSeconds assumes. A
    // NIP-17 DM is NIP-44-encrypted twice - once sealing the rumor (kind
    // 14, our message), again wrapping the seal for delivery - and
    // NIP-44 v2 caps plaintext at 65,535 bytes and pads it into
    // fixed-size buckets before encrypting (see nips.nostr.com/44's
    // calc_padded_len). Working back from the outer (gift wrap) layer's
    // cap to how much raw audio that leaves room for:
    //
    //   65,535                          NIP-44 plaintext cap (gift wrap layer)
    //   -    490                        seal event JSON overhead (id/pubkey/tags/sig/...)
    //   =  65,045
    //   /    4/3                        undo base64 on the seal's own ciphertext
    //   ≈  48,784                       budget for the seal's padded ciphertext
    //
    // calc_padded_len rounds *up* to the next bucket, and the bucket
    // above 40,960 is 49,152 - which doesn't fit in 48,784 - so the
    // largest usable padded length is the bucket below, not 48,784
    // itself:
    //
    //   40,960                          largest padded length that fits (bucket, not 48,784)
    //   -    430                        rumor JSON overhead (kind/tags/created_at/...) + prose
    //   =  40,530
    //   /    4/3                        undo base64 on the audio data URI
    //   ≈  30,400                       MaxAudioBytes
    //
    // A fixed, verified constant rather than a general formula: the
    // bitrate above isn't user-configurable, so there's exactly one
    // scenario to get right here, and hand-implementing calc_padded_len
    // generically would be a second, harder-to-verify source of bugs for
    // no practical benefit. See docs/voicemail.md.
    public const int MaxAudioBytes = 30_400;

    // MaxRecordingSeconds is deliberately NOT MaxAudioBytes /
    // (OpusBitrateBps / 8) - that naive division assumes the encoder
    // produces exactly OpusBitrateBps and ignores the Ogg container
    // itself (OpusHead/OpusTags pages, per-page headers and lacing
    // tables). Verified empirically (throwaway encode of a 30s tone at
    // OpusBitrateBps with VBR off): actual output ran ~1,050-1,060
    // bytes/sec against a naive estimate of 1,000, i.e. a 30s recording
    // produced ~31.8KB - over MaxAudioBytes despite "fitting" the naive
    // math. Reserving 10% of MaxAudioBytes for that overhead, plus slack
    // for CallerNumber (bounded by PhoneNumberNormalizer, but still
    // variable-length) eating into the rumor-overhead assumed above,
    // keeps the *configured* ceiling comfortably under the *enforced*
    // one (VoicemailSender.LoadAudioAsync still checks the real encoded
    // size against MaxAudioBytes regardless, since a reserve is a
    // heuristic, not a proof).
    private const int ReservedAudioBytes = MaxAudioBytes * 9 / 10;

    public const int MaxRecordingSeconds = ReservedAudioBytes / (OpusBitrateBps / 8);
}
