namespace Sip2Nostr.Shared;

// A startup failure that's the operator's to fix - a bad config value, a
// port already in use - as opposed to an unexpected bug. Program.cs
// catches this type specially: a single clean fatal log line and a
// non-zero exit, no stack trace dump - there's nothing in one that would
// help diagnose a bad config value or a busy port, only noise that
// obscures the actionable message.
public sealed class ConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
