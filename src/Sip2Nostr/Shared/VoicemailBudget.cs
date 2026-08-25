namespace Sip2Nostr.Shared;

// Shared by VoicemailSender (encodes at OpusBitrateBps, enforces
// MaxAudioBytes) and ConfigLoader (validates max_recording_seconds
// against MaxRecordingSeconds at startup). See docs/voicemail.md for
// the full derivation.
public static class VoicemailBudget
{
    public const int OpusBitrateBps = 8000;
    public const int MaxAudioBytes = 30_400;

    private const int ReservedAudioBytes = MaxAudioBytes * 9 / 10;
    public const int MaxRecordingSeconds = ReservedAudioBytes / (OpusBitrateBps / 8);
}
