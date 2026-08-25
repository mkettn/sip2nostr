# Caller Allow/Deny-List

Every inbound SIP call is checked against a blacklist/whitelist before any
WebRTC/RTP media setup or Nostr signaling happens. This is a courtesy
filter to cut down on unwanted rings, **not a real ACL**: the SIP `From`
header is trivially spoofable by a malicious upstream/carrier, so treat
this as "stop accidental noise," not "stop a determined attacker."

## Flow

1. `Sip/SipCallSource.cs`, right after `ua.AcceptCall(inviteRequest)` and
   before any media session is created: extract the caller's number from
   `inviteRequest.Header.From.FromURI.User`.
2. Normalize it (`CallerList/PhoneNumberNormalizer.cs`).
3. Ask the configured `CallerList/CallerListGate.cs` whether the
   normalized number is allowed.
4. If not allowed, reject the SIP call outright
   (`uas.Reject(SIPResponseStatusCodesEnum.Forbidden, null)`) and return —
   no RTP/WebRTC session is ever created for a blocked caller, and no
   Nostr signaling event is ever sent.

Every step of this logs at `Information` level, so a blocking decision can
be confirmed directly from the logs: `ConfigCallerListProvider` logs which
list (if any) a number matched, `CallerListGate` logs each provider's
verdict, and `SipCallSource` logs the normalized number and the final
reject/proceed decision.

## Normalization

`PhoneNumberNormalizer.Normalize` keeps only digits, dropping any leading
`+` or `00` international-prefix marker entirely (folding `00` the same
way a leading `+` would be), so a caller isn't treated as a different
identity depending on which prefix form — or none at all — the provider
happens to send. The result never carries a leading `+`: an earlier
version of this normalizer kept it when present, which meant `"+49...".`
and `"49..."` normalized to two different strings even though they're the
same number, silently breaking blacklist/whitelist matching whenever a
trunk sent bare digits with no `+`. It also strips a `tel:` URI's
`;phone-context=...` suffix if present. Examples:

| Raw `From` user part | Normalized |
|---|---|
| `+49 30 12345` | `493012345` |
| `0049-30-12345` | `493012345` |
| `493012345;phone-context=+49` | `493012345` |

## Extensibility: `ICallerListProvider`

`SipCallSource` depends only on `CallerList/CallerListGate.cs`, never on a
concrete list source. The gate takes any number of
`ICallerListProvider` implementations; today `SipCallSource` wires up
exactly one — `ConfigCallerListProvider`, reading `[callerlist]` from
`config.toml`. Any provider returning `Deny` blocks the call.

This is deliberate: adding a future source (a CardDAV address book, a
Google Contacts sync) is a `SipCallSource`-only change — implement
`ICallerListProvider`, construct it alongside `ConfigCallerListProvider`,
pass both into `CallerListGate`. `SipCallSource` and the gate's contract
don't change.

## Config (`[callerlist]` in `config.toml`)

```toml
[callerlist]
blacklist = ["+491234567890"]
whitelist = []
```

**Precedence** (implemented in `ConfigCallerListProvider`):
- A blacklist match always denies, checked first.
- An empty whitelist means blacklist-only mode: everyone not blacklisted
  is allowed.
- A non-empty whitelist switches to deny-by-default: only whitelisted
  numbers get through (a blacklist match still overrides).

## Blind spots

- No CardDAV/Google Contacts provider yet — config is the only source.
- No dynamic reload. Caller lists are an in-memory snapshot loaded at
  startup, like the rest of `config.toml`.
- Courtesy filter only, as noted above — `From` is spoofable, so this
  doesn't stand in for real caller authentication.
