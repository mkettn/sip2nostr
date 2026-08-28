# Agent instructions

This repo is developed with AI coding agents in two distinct roles:
**coding agents** that implement changes, and **review agents** that
review them (on PRs, or standalone). Each role gets its own section below
because the two personas can pull in opposite directions - a coding agent
optimizing for "get this merged" and a review agent optimizing for
"nothing slips through" need different defaults to stay useful rather
than fight each other.

## Coding agent

### Stay inside the scope you were given

Do exactly what was asked, in the files that are actually relevant to it.
A general principle stated in passing ("docs shouldn't say X") is not
authorization to sweep every file that happens to contain X - if you
notice something out of scope while working, mention it or file an issue
instead of fixing it inline. When asked to touch "the hub architecture
docs," that means the doc(s) actually about the hub architecture, not
every doc that could arguably be tidied under the same principle. If
you're unsure whether something is in scope, it's cheaper to ask than to
revert an unwanted edit later.

### Docs describe the present, not the history

Documentation in `docs/*.md` and `README.md` describes how the software
works *now* (and, where relevant, what's planned next). It does not
narrate what a previous design decision was, what got reverted, or what
didn't work before "since the refactor," "previously this used X," "we
used to do Y" have no place here outside of a rare, explicitly-requested
migration note. Read the surrounding doc for its existing voice before
adding to it; match it rather than introducing a different register.

### Comments explain WHY, never WHAT

Default to no comments. Add one only when the *why* isn't obvious from
the code itself - a hidden constraint, a subtle invariant, a workaround
for a specific upstream bug, a non-obvious ordering requirement. Never
add a comment that just restates what the next line does, references the
current task/issue/PR number, or explains what a well-named
type/method already makes clear. See `docs/hub-architecture.md` and
`Shared/VoicemailBudget.cs` for the level of comment density this
codebase aims for: sparse, but load-bearing where present.

### Respect the hub/source/sink architecture's invariants

See `docs/hub-architecture.md` for the full design. Two invariants have
already been actively defended once each and are easy to accidentally
undo while "improving" something nearby:

- **`ICallAudio`'s exchange format is RTP, not PCM.** The hub relays raw
  RTP frames between sources and sinks; decoding to PCM and re-encoding
  is a sink/source-boundary concern (e.g. `WhisperNetTranscriber`
  decoding a WAV file it already owns), never something the hub or
  `ICallAudio` itself does. Don't reintroduce a PCM decode/re-encode step
  in the relay path to make some other piece of code more convenient.
- **A `Call` handed to a sink is always already answered**, with audio
  flowing (`Call.cs`'s doc comment states this explicitly). Sinks must
  not assume they get to decide *whether* to answer - only what to do
  once a call is live. (This invariant is the reason issue #18 - ringback
  before answer - is a real architecture change and not a one-line fix;
  read that issue before attempting it.)

### Config validates fail-fast at load, not at first use

New `[section]` config values go through `ConfigLoader.Validate`, which
throws `InvalidDataException` with a specific, actionable message at
startup - not lazily the first time the value is used. Follow the
existing style: check cheap/structural things first (enum-like string
values, required-when-X fields) before anything that depends on them
(e.g. validate `delivery` is a known value *before* using it to pick a
size ceiling - see `ConfigLoader.cs`). A new default should preserve
today's behavior unless the user explicitly opts in to something else.

### Never trust a duration/count heuristic for an output-size budget

Any time an encoded/transcribed/generated output has to fit a real
external size limit (the NIP-17 DM budget in this codebase, see
`VoicemailBudget.cs`), check the *actual* output's size against the
budget and fail loudly and specifically if it doesn't fit. Don't assume
a duration or input-size cap keeps the output small - encoders and
transcription engines have failure modes (repetition loops, worst-case
expansion) that break that assumption. This was learned the hard way once
on the audio path and had to be relearned for the transcript path; don't
make a third team relearn it again for the next output type.

### Build and test both projects before every commit

There is no top-level `.sln` file. Build and test each project
individually:

```
dotnet build src/Sip2Nostr/Sip2Nostr.csproj
dotnet test tests/Sip2Nostr.Tests/Sip2Nostr.Tests.csproj
```

A clean build (0 warnings, not just 0 errors) and a fully passing test
suite are the bar before pushing, not just "it compiles." Add tests for
new `ConfigLoader` validation branches and any new pure logic - see
`tests/Sip2Nostr.Tests/ConfigLoaderTests.cs` for the existing pattern
(temp TOML files, one behavior per test).

### File issues instead of scope-creeping a PR - but ask before deferring

When you or a reviewer notice a real improvement that isn't required by
the current PR's stated goal, the default is still to keep it in scope
if it's small enough to do without derailing the PR's actual goal - not
to reach for a new issue as the easy way out. When it genuinely doesn't
belong in this diff (too large, too risky, orthogonal to what the PR is
for), don't unilaterally decide to postpone it to a separate issue and
move on - say so and ask the user first. Only once they agree it should
be deferred, write it up as a GitHub issue (current behavior / desired
behavior / why it's out of scope here). Keep PRs reviewable and scoped
to what they claim to do; let the issue tracker hold the backlog, but
only with the user's say-so on what goes there.

### Keep a PR's diff to a reviewable size

Aim for at most ~1,200 changed/added lines per PR. Above ~3,000, the PR
is probably too big - split it into smaller PRs that land independently,
or check with the user before pushing on. The one exception is a big new
feature whose size is mostly necessary boilerplate (generated bindings,
a new project's scaffolding, a large but mechanical rename) rather than
logic a reviewer actually has to reason about line by line - call that
out explicitly rather than assuming it's self-evident.

## Review agent

*(Rules for the review persona go here - maintained by a separate
instance operating in that role.)*
