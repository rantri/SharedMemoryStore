# CR-M06 Versioned Spill-Summary Qualification

**Result: Passed on Windows x64**

This qualification compares the original sticky overflow hint with the
required-feature-bit, exact-generation versioned-empty `SpillSummary`. Both raw
reports record the same repository commit, host, runtime, store size, collision
set, churn count, missing-key sample count, and trial count. The working tree is
intentionally dirty because the feature is not committed; the final after
artifact therefore also records exact managed-assembly hashes.

## Environment and command

- Repository commit: `0cf7a43f9c39de1691b237a9761035339edd0964`
- OS: Microsoft Windows `10.0.26200`, x64
- Runtime: .NET `10.0.5`, x64
- CPU: Intel Family 6 Model 183, 32 logical processors
- Store: 4,096 slots
- Workload: 17 published exact bucket-pair collisions, 10,000 complete
  publish/remove cycles, 16,384 early and 16,384 late missing-key samples
- Trials: 3

```powershell
dotnet run -c Release --no-build `
  --project benchmarks/SharedMemoryStore.SyncProbe/SharedMemoryStore.SyncProbe.csproj -- `
  --mode overflow --profile v2 --overflow-slot-count 4096 `
  --overflow-churn-cycles 10000 --overflow-missing-samples 16384 `
  --trials 3 `
  --output artifacts/sync-probe-cr-m06-versioned-overflow-qualification.json
```

## Raw artifacts

| Artifact | SHA-256 |
|---|---|
| [Before: sticky hint](../../../artifacts/sync-probe-cr-m06-sticky-overflow-qualification.json) | `45FD4E953B691CCFBCD7863449CE78A824699961BAFDE676FDC85ACE551637DB` |
| [After: versioned empty](../../../artifacts/sync-probe-cr-m06-versioned-overflow-qualification.json) | `54ECECAFB0807839ECC5AFDB5EA7A346739D23DBAF967B49AAC89D46136EC477` |

The after artifact is report schema 6, 1,586,081 bytes, written at
`2026-07-13T13:30:28.9165932+03:00`. It records
`RepositoryWorkingTreeState=dirty`, `SharedMemoryStore.dll` SHA-256
`8501A6A9F71A08B2901DA3F41B7F945AB7E557D12DC092183A9573CA769F45BF`,
and `SharedMemoryStore.SyncProbe.dll` SHA-256
`2A67E54D4C7EF793486B35DDD0D9F5923A6DC3FE7C225CE358208D7EB8108F79`.

## After-fix trial evidence

| Trial | Failures | Spill during / after first cleanup / after churn | Overflow occupancy during / after first cleanup / after churn | Cleanup scans before -> after | Full-scan max | Late-window scans before -> after | Early p99 (us) | Late p99 (us) | Ratio |
|---:|---:|---|---|---|---:|---|---:|---:|---:|
| 1 | 0 | `1 / 0 / 0` | `1 / 0 / 0` | `1 -> 3` | 4,096 | `30,000 -> 30,000` | 0.5 | 0.2 | 0.4x |
| 2 | 0 | `1 / 0 / 0` | `1 / 0 / 0` | `1 -> 3` | 4,096 | `30,000 -> 30,000` | 0.2 | 0.2 | 1.0x |
| 3 | 0 | `1 / 0 / 0` | `1 / 0 / 0` | `1 -> 3` | 4,096 | `30,000 -> 30,000` | 0.2 | 0.2 | 1.0x |

All three diagnostics gates and latency gates are true. The late window added
exactly zero overflow scans in every trial, and the median late/early p99 ratio
was `1.0x`, within the `2.0x` limit. The exact current summary witness fast path
also reduced churn scan invocations from the independently reviewed
pre-fast-path run's 200,000 per trial to 30,000; primary-only mutations retain
Present without scanning the overflow table.

## Before/after conclusion

The original artifact retained one logical spilled bucket after overflow
occupancy returned to zero. Its three late/early p99 ratios were `61.6x`,
`148.5x`, and `262x` (median `148.5x`), and all diagnostics qualifications
failed. With versioned-empty cleanup, the summary reaches logical Empty after a
stable real full scan, the late missing-key path performs no overflow scan, and
the median ratio falls to `1.0x` with zero correctness failures. CR-M06 is
therefore resolved for this qualified Windows x64 environment.

## Focused regression evidence

- Spill summary, operation-budget equality, and checkpoint unit tests: 26/26
  passed in Release.
- Layout and diagnostics contract tests: 36/36 passed in Release.
- Full canonical checkpoint kill/recovery matrix: 45/45 passed in Release,
  including immediate post-recovery empty spill/occupancy and zero-scan checks
  for spill-summary IDs 41-44, plus participant-recovery ID 45.
- Full solution regression: 696/696 passed in Release (unit 232, contract 113,
  integration 229, interop 75, linearizability 47).
- SyncProbe Release build: 0 warnings, 0 errors.
