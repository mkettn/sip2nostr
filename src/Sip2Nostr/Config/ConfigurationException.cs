namespace Sip2Nostr.Config;

// A startup failure that's the operator's to fix - a bad config value, a
// port already in use - as opposed to an unexpected bug. Program.cs
// catches this type specially: a single clean fatal log line and a
// non-zero exit, no stack trace dump - there's nothing in one that would
// help diagnose a bad config value or a busy port, only noise that
// obscures the actionable message. Raised outside ConfigLoader too (a
// busy SIP port, an unreachable Nostr relay) - those are still config's
// concern in spirit, the same "the operator needs to fix something before
// this can run" class of failure, just not caught by ConfigLoader itself.
public sealed class ConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
