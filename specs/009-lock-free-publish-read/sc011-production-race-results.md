# SC-011 Production Race Evidence

**Root seed**: `1592590848`
**Authoritative command**: `scripts/run-lock-free-qualification.ps1`

## Evidence boundary

SC-011 counts only invocations of the real mapped `MemoryStore` action pair. A
reference-model permutation is checker coverage, not a production race. A
generated history is linearizability evidence, not a high-count production
race. The qualification runner records all three counts independently and
rejects missing, duplicate, renamed, or wrong-seed completion markers.

The required production matrix has eight families:

| Family marker | Contracted action pair |
|---|---|
| `publish-publish` | atomic publication / atomic publication |
| `publish-reserve` | atomic publication / explicit reservation |
| `reserve-reserve` | explicit reservation / explicit reservation |
| `commit-acquire` | reservation commit / acquire |
| `acquire-remove` | acquire / remove |
| `release-reclaim` | final lease release / same-key reclaim and reuse |
| `recovery-live-lease` | normal recovery with overrides disabled / live lease release |
| `disposal-operation` | local handle disposal / acquire on that exact handle |

Each non-disposal family reuses persistent actor threads and a two-actor start
barrier, but executes both production calls once per repetition. The disposal
family keeps only the mapping and an independent keeper handle persistent. It
opens a fresh participant handle and races exactly one `Dispose` call with one
operation for every repetition. Therefore `completed=N`, `disposeCalls=N`, and
`freshHandles=N`; operations are not credited multiple times against one
long-lived disposal interval. The keeper verifies after every race that another
handle can still read the exact payload.

Normal recovery uses `RecoverCurrentProcessLeases: false`. A live exact owner
must never be recovered, failed, or classified unsupported; its concurrent
release remains `Success`. The current-process administrative override is
intentionally absent because racing that override with live lease use is outside
the supported contract.

## Qualification command

The nightly and release tiers set the count to at least one million per family:

```powershell
pwsh -NoProfile -File scripts/run-lock-free-qualification.ps1 `
  -Tier release `
  -OutputDirectory artifacts/lock-free-qualification-release `
  -AdditionalOsEvidence artifacts/lock-free-os-validation/linux-x64-final.json
```

The generated run directory contains immutable stdout for the
`production-race-stress` step plus `summary.json`. The runner verifies one exact
derived-seed marker for every family and records a total of 8,000,000 real
production races when the configured count is 1,000,000.

## Convergence stress

A pre-release Windows x64 convergence run on 2026-07-13 executed 100,000 real
races in every family (800,000 total) and passed in 44.3 seconds. The disposal
family executed 100,000 fresh opens and 100,000 `Dispose` calls, reaching both
documented orderings (`operationWins=49,358`, `disposalWins=50,642`). This run is
diagnostic; only a passing runner-owned million-per-family artifact is credited
to the release gate.

The earlier six-family artifact is withdrawn. It omitted both reservation
arbitration families, used the current-process recovery override concurrently
with live lease activity, and credited one disposal interval as one million
races. None of those counts is used by the current runner.

## Production-captured histories

The production-history tier independently captures all eight families. Each
history contains 6-12 real calls from 2-4 actors, retains invocation/entry/return/
response envelopes and real reservation or lease tokens, and is checked by the
reference model. A failure is minimized and reported with its root and derived
family seed. The reference-only tier includes the same two reservation
arbitration families and models normal same-participant recovery as preservation,
not administrative override recovery.

## Regressions exposed during convergence

The production harness previously found two release/reclaim defects that a
model-only permutation count did not expose:

1. A second directory lookup could observe final reclamation and leak the
   internal absent result as `NotFound` from same-key publication.
2. Capacity could become reusable between the first claim and helper scan, but
   publication did not re-probe after successful helping and returned transient
   `StoreFull`.

The engine now treats second-lookup absence as permission to continue and
re-probes capacity after successful reclaim helping. The fixed-seed production
stress keeps `NotFound` and unjustified `StoreFull` outside the family oracle.
