# Reviewing sip2nostr changes

Repo facts - architecture invariants, build and test commands, config
validation style - live in `AGENTS.md`. Read that first; this file is about
how to review, not what the codebase is.

## Verify claims, don't trust them

Source comments and PR descriptions assert things about third-party libraries
that are frequently wrong. Check the package or its source before treating a
claim as fact, and say which findings are verified and which are inferred.

Three real cases from this repo: `AudioExtrasSource` was asserted to be
`IDisposable` across five review rounds and isn't (it implements only
`IAudioSource`; `CloseAudio()` is the whole teardown). `Concentus.Oggfile`
1.0.7 looks stale from its version number but targets Concentus 2.2.2 with a
current `net8.0` target. `OpusOggWriteStream` genuinely does reject a
DTX-enabled encoder, which is why the obvious "just enable DTX" suggestion
was wrong.

## Re-derive size budgets

`src/Sip2Nostr/Shared/VoicemailBudget.cs` encodes a NIP-44 derivation. When a
change touches those constants, recompute rather than trusting the comment.
The `calc_padded_len` bucketing step is the one that gets skipped, and it has
put a doc off by ~60% before.

## For refactors, check behaviour preservation explicitly

Diff the new code against what it replaced, not only against itself. Hand-
rolled substitutes for library code are where regressions hide: both RTP
pacing and timestamp continuity broke when `AudioExtrasSource` was replaced
with a hand-written pacer, and neither showed up in tests.

For a rebase, `git range-diff <old-base>..<old-tip> <new-base>..<new-tip>`
separates the PR's own changes from what the new base brought in.

## Check the loop actually closed

- Did CI run at all? A PR with zero check runs is itself a finding.
- On a follow-up commit, confirm each earlier finding is fixed rather than
  discussed, and say plainly which ones weren't.
- When a comment defers to a doc ("see docs/X.md for why"), confirm the
  pointer resolves and that the doc carries the reasoning the comment
  dropped.

## Report

Problems first, each with a concrete fix. State what you verified and what
you couldn't (no hardware, no `dotnet` in the environment). Don't restate
what's fine, and don't pad with praise - noting that something is correct is
worth it only when it looks wrong at a glance or when a prior review got it
wrong.
