# Agent instructions

This file is loaded into every agent session on this repo, so it holds what
any agent needs regardless of what it's doing: how the code is built and
tested, the architecture invariants that are easy to undo by accident, and
the conventions this codebase actually follows. The coding-agent defaults
below are here too, since implementing a change is the common case.

Rules for reviewing changes live in `agents/reviewing.md`, so any tool can
read them; `.claude/skills/review/SKILL.md` is a pointer that makes Claude
Code load them only when the task is a review. Keeping them out of this file
means a reviewer's defaults - which pull the opposite way from a coding
agent's - never sit in the same context as the rules they'd contradict.

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
didn't work before. "Since the refactor," "previously this used X," "we
used to do Y" have no place here outside of a rare, explicitly-requested
migration note. Read the surrounding doc for its existing voice before
adding to it; match it rather than introducing a different register.

### Comments explain WHY, never WHAT

Default to no comments. Add one only when the *why* isn't obvious from
the code itself - a hidden constraint, a subtle invariant, a workaround
for a specific upstream bug, a non-obvious ordering requirement. Never
add a comment that just restates what the next line does, references the
current task/issue/PR number, or explains what a well-named
type/method already makes clear. Most of the codebase is sparse on
comments by default - that's the target density everywhere else.
`src/Sip2Nostr/Shared/VoicemailBudget.cs` is the exception, not the
example to match generally: its constants encode a real NIP-44 byte-
budget derivation that has to be written down somewhere, so it's
deliberately comment-heavy. Match its density only when you're
documenting a similarly non-obvious derivation, not as a general target.

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
  flowing (`src/Sip2Nostr/Hub/Call.cs`'s doc comment states this
  explicitly). Sinks must not assume they get to decide *whether* to
  answer - only what to do once a call is live. (This invariant is the reason issue #18 - ringback
  before answer - is a real architecture change and not a one-line fix;
  read that issue before attempting it.)

### Config validates fail-fast at load, not at first use

New `[section]` config values go through `ConfigLoader.Validate`, which
throws `InvalidDataException` with a specific, actionable message at
startup - not lazily the first time the value is used. Follow the
existing style: check cheap/structural things first (enum-like string
values, required-when-X fields) before anything that depends on them
(e.g. validate `delivery` is a known value *before* using it to pick a
size ceiling - see `src/Sip2Nostr/Config/ConfigLoader.cs`). A new
default should preserve today's behavior unless the user explicitly
opts in to something else.

### Never trust a duration/count heuristic for an output-size budget

Any time an encoded/transcribed/generated output has to fit a real
external size limit (the NIP-17 DM budget in this codebase, see
`src/Sip2Nostr/Shared/VoicemailBudget.cs`), check the *actual* output's
size against the budget and fail loudly and specifically if it doesn't
fit. Don't assume a duration or input-size cap keeps the output small -
encoders and
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

Measure size the way GitHub's diff stat does: additions plus deletions
combined (a PR at "+1,200 -0" and one at "+700 -500" are both ~1,200 by
this measure). Aim for at most ~1,200 by default. Above that, it should
still be one coherent, reviewable change and not a grab-bag - a bigger
number is fine when the PR is doing one real thing that doesn't split
cleanly, such as a refactor whose intermediate states would leave the
build or tests broken (the hub-architecture rework in #16 landed at
~2,146 for exactly this reason). Above ~3,000, or if you're unsure
whether the size is still justified, say so and check with the user
before pushing on rather than deciding alone. The "boilerplate" exception
(generated bindings, a new project's scaffolding, a large but mechanical
rename) is separate from the "can't be split" exception above - either
one is a reason to go over ~1,200, but call out explicitly which one
applies rather than assuming it's self-evident.
