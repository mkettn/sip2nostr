# Agent instructions

This repo is developed with AI agents. Everything below applies to any
coding session on this repo. For reviewing changes rather than making
them, see `agents/reviewing.md` (loaded automatically by the `review`
skill).

## Working in this repo

### Stay inside the scope you were given

Do exactly what was asked, in the files actually relevant to it. A
general principle stated in passing isn't authorization to sweep every
file that could arguably apply. If unsure whether something is in
scope, ask rather than guess.

### Docs describe the present, not the history

`docs/*.md` and `README.md` describe how the software works now (and,
where relevant, what's planned next) - not what changed, what got
reverted, or what used to be true. Match the surrounding doc's existing
voice.

### Comments explain WHY, never WHAT

Default to no comments. Add one only when the *why* isn't obvious from
the code - a hidden constraint, a subtle invariant, a workaround, a
non-obvious ordering requirement. Never restate what the next line
already says. `src/Sip2Nostr/Shared/VoicemailBudget.cs` is a deliberate
exception: its constants encode a NIP-44 size derivation that has to be
written down somewhere.

### Respect the hub/source/sink architecture's invariants

See `docs/hub-architecture.md` for the full design.

- `ICallAudio`'s exchange format is RTP, not PCM. The hub relays raw RTP
  frames between sources and sinks; decoding/re-encoding is a
  sink/source-boundary concern, never the hub's.
- A `Call` handed to a sink is still ringing, not yet answered
  (`src/Sip2Nostr/Hub/Call.cs`). A sink decides whether and when to call
  `AnswerAsync` - that decision belongs to the sink, not the hub or
  source - and no audio flows until it does. See `docs/hub-architecture.md`'s
  "Who answers, and when" for what each existing sink does with that.

### Config validates fail-fast at load, not at first use

New `[section]` config values go through `ConfigLoader.Validate`, which
throws `Sip2Nostr.Config.ConfigurationException` with a specific message
at startup. Check cheap/structural things (enum values, required-when-X
fields) before anything that depends on them. New defaults preserve
today's behavior.

`ConfigurationException` is also how any other startup failure the
operator can actually fix - not just a bad config value - reaches
`Program.cs`'s top-level catch: it logs the message alone, no stack
trace, and exits non-zero, instead of the full-trace crash dump an
unexpected bug gets. Use it for that kind of failure wherever it's
raised, not only inside `ConfigLoader` itself.

### Never trust a duration/count heuristic for an output-size budget

When an encoded/transcribed/generated output has to fit a real size
limit (see `src/Sip2Nostr/Shared/VoicemailBudget.cs`), check the actual
output's size against the budget and fail loudly if it doesn't fit.
Don't assume a duration or input-size cap keeps the output small.

### Build and test before every commit

```
dotnet build
dotnet test
```

Runs against `Sip2Nostr.sln` from the repo root - both projects at
once, no path needed. 0 warnings and a fully passing suite, not just
"it compiles." Add tests
for new `ConfigLoader` validation branches and new pure logic (see
`tests/Sip2Nostr.Tests/ConfigLoaderTests.cs`).

### Ask before deferring work to a new issue

Prefer keeping a small, related fix in scope. When something genuinely
doesn't belong in the current diff, ask the user before filing an issue
for it - don't decide unilaterally.

### Keep a PR's diff to a reviewable size

Aim for at most ~1,200 changed lines (additions + deletions) per PR.
Above ~3,000, or if the size feels unjustified, check with the user
rather than deciding alone. Exceptions: necessary boilerplate, or a
coherent change that can't be split without broken intermediate states -
say which one applies.
