# Lock-Free Checkpoint Crash-Recovery Evidence

## Directory publication/revalidation extension (2026-07-14)

The append-only catalog now contains 67 checkpoints. Checkpoint 66 pauses after
an Empty-location source tuple has been revalidated and before the location CAS;
checkpoint 67 pauses after location publication and before source
revalidation. They make cancellation handoff, first-publisher arbitration,
post-CAS withdrawal, alternate-location cleanup, and generation-reuse behavior
crash-observable through the production callback.

Focused Release runs of the complete current catalog passed 67/67 on Windows
x64 and 67/67 on Linux x64 (WSL2). The corresponding final 8-process fixed-key
churn regression passed on both platforms, and the full Release aggregate on
each platform passed 985/985 tests with zero failures or skips. These worktree
runs establish implementation/review closure; the immutable final qualification
workflow remains the release-evidence authority.

## Projection and recovery-window extension (2026-07-14, historical)

At this intermediate stage the append-only catalog contained 65 checkpoints.
IDs 62-65 covered canceled
insert cleanup, the no-active-lease reclaim proof, participant generation
advance, and the lease-projection metadata/control revalidation window. A
focused Release run of that complete catalog passed 65/65 on Windows
x64. The earlier Linux x64 run below covered the then-current 61-entry catalog;
the current 67-entry result above supersedes that former Linux gap.

## PID-namespace checkpoint extension (2026-07-14)

At the time of this extension the append-only catalog contained 61 checkpoints.
Checkpoint 61 pauses after
the per-record PID-namespace write and before Active publication. Focused
Release runs of the complete 61-case crash catalog passed 61/61 on Windows x64
and 61/61 on Linux x64 (WSL2). The Linux namespace-focused unit set passed
55/55 and the focused profile/open integration set passed 11/11. These focused
runs verify routing and crash recovery; final tier artifacts remain recorded by
the qualification workflow.

**Date**: 2026-07-13
**Branch**: `codex/lock-free-csharp`
**Evidence commit base**: `0cf7a43` plus the feature worktree
**Host**: Microsoft Windows 10.0.26200, x64 (`win-x64`)
**SDK/runtime**: .NET SDK 10.0.201, .NET runtime 10.0.5, x64

## Release command

```powershell
dotnet build tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --no-restore
dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --no-build --filter "FullyQualifiedName~LockFreeCrashRecoveryIntegrationTests" --logger "console;verbosity=minimal"
```

Result:

- build: PASS, 0 warnings and 0 errors;
- checkpoint crash matrix: PASS, 31/31 tests, 0 skipped, approximately 3 seconds test duration;
- repeat stability check: PASS, 3 consecutive runs and 93/93 checkpoint cases;
- process termination mode: `Process.Kill(entireProcessTree: true)` at the exact instrumented protocol checkpoint.

## Matrix coverage

`LockFreeCrashRecoveryIntegrationTests.CanonicalCheckpoints` enumerates the
production `LockFreeCheckpointCatalog.Entries` collection directly. Therefore a
new catalog entry automatically adds a required crash case instead of relying on
a duplicated test list.

| Checkpoint family | Catalog entries exercised |
|---|---:|
| Publish | 2 |
| Reserve | 2 |
| Commit | 2 |
| Abort | 2 |
| Acquire | 2 |
| Project | 2 |
| Release | 2 |
| Remove | 2 |
| Reclaim | 3 |
| Directory | 4 |
| Diagnostics | 2 |
| Recovery | 2 |
| Disposal | 2 |
| Participant | 2 |
| **Total** | **31** |

For every entry, the child opened the real friend-instrumented layout-v2 engine,
emitted one JSON checkpoint signal, and blocked inside the production callback.
While it was stopped, the controller published, acquired, checked, released,
and removed an unrelated key. The controller then killed the child, ran explicit
lease and reservation recovery with current-process recovery disabled, and
required all recovery passes to converge without a failed-recovery count.

## Safety and capacity evidence

- **Live-owner preservation**: the controller retained a live lease during every
  child pause and recovery. Recovery continued to report at least one active
  lease, and the controller verified the original bytes before releasing it.
- **Killed-token fencing**: outside participant-registration checkpoints, the
  child captured an independent lease handle before entering its target
  operation. After kill and recovery, the controller reconstructed that
  adversarial internal token and proved it was invalid and could not release a
  later lease-record incarnation.
- **Local stale-copy fencing**: a copied controller lease was released, the same
  table was reused, and the copied token could not release the replacement.
- **Value capacity restoration**: after cleanup, all 4 configured slots accepted
  publications and the fifth returned `StoreFull`.
- **Lease capacity restoration**: all 4 configured lease records could be active
  simultaneously and the fifth acquire returned `LeaseTableFull`.
- **Participant capacity restoration**: with the controller handle open, 7 more
  handles opened successfully for the configured capacity of 8 and the ninth
  returned `ParticipantTableFull`. This passed after crashes at both
  `ParticipantAfterActivePublication` and `DisposalAfterParticipantRelease`,
  covering stale active participants that had no remaining slot/lease reference.
- **No store-wide failure**: no case returned `CorruptStore`, leaked recoverable
  slot/lease capacity, invalidated the live owner, or prevented unrelated-key
  progress.

## Original platform qualification boundary (2026-07-13)

This historical run qualified the portable checkpoint pause and process-kill recovery mode
on the current Windows x64 host. Linux x64 `SIGSTOP`/`SIGCONT` and Docker
pause/resume/kill were not available in this Windows run and remain **not
qualified** by this historical section; the later Linux current-catalog result
is recorded above, while final platform qualification remains machine-derived
from the immutable release evidence.
