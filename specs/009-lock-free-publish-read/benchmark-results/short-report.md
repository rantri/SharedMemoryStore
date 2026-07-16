# Short Convergence Report

## Frozen conditional conclusion

This report is frozen before the one-shot runs and is not edited afterward.
The short convergence conclusion is **PASS if and only if** the final PR
[`summary.json`](../../../artifacts/lock-free-qualification/009-final-r6-pr/summary.json)
has schema 4, tier `pr`, `validationOnly: false`, and `overallStatus: passed`,
and its hashed
[`sync-probe.json`](../../../artifacts/lock-free-qualification/009-final-r6-pr/sync-probe.json)
passes every exact row, count, provenance, correctness, raw-visibility, and
short-performance check enforced by the runner. Its start/completion provenance
must be identical and clean, and must match the final nightly and release
summaries. Otherwise the short conclusion is **FAIL** or **NOT QUALIFIED** as
reported by the raw JSON; this tracked report cannot promote it.

The same clean commit then has to pass the immutable
[`009-final-r6-nightly`](../../../artifacts/lock-free-qualification/009-final-r6-nightly/summary.json)
and
[`009-final-r6-release`](../../../artifacts/lock-free-qualification/009-final-r6-release/summary.json)
gates before the short result can contribute to release qualification. Raw
JSON remains in the ignored immutable `artifacts/` tree; no copy is maintained
under tracked `specs/` and no tracked post-run edit is permitted.

## Current diagnostic evidence

Historical snapshots have passed Windows OS release tests, bounded SC-011
production races and production-generated histories, Windows native/Python
validation, and focused Linux tests. Their exact counts are snapshot-specific
and are not asserted for the current tree. These are diagnostic results only:
they are not one provenance-matched final sequence and do not establish any
final performance threshold.

The earlier clean R2 short-tier diagnostic, `009-r2-pre-final4-pr`, passed all 24
qualification rows on commit `ca200238423877044d841a3b92f93edb37385d46`:
1,014/1,014 tests, the one exact 10,000-cycle churn row, all 50 directory
configurations, and 108/108 suspension rows. Its summary SHA-256 is
`3D543FBF5E4C4A5C514C1C3309565F0B2575126E5B176B79E347802AC46E331A`
and its synchronization-probe SHA-256 is
`9788BF7222BFE8B77E4512EECFEE9788BD1D898763AABBA0417F16D2AF8DE3EC`.
This is a successful convergence diagnostic, not the frozen final PR result.

The attempted R2 Linux final is preserved with SHA-256
`090651C119CADD7DAC2C545D04F595FD9E65F5CEFAFC1B9B79EDA009F66EAA7C`.
It was launched on Windows, correctly reported `not-qualified`, and contributes
no final Linux evidence. The corrected Ubuntu invocation passed Linux-x64
validation-only orchestration with SHA-256
`AE16F3AED1A9E0FE5113F20AD3AB2F28AE194E5CF0717F6372BF87362A51666C`;
that is an invocation check only, not a benchmark or final result.

The R3 Linux attempt is also preserved, SHA-256
`59419F78D559829A0E3B47AE1FAF334B5B79E83CAF8004646A1FE3687C5B86D5`.
It reached Linux x64 but stopped at `dotnet --info` before workloads because a
stale user-local workload manifest set remained after an SDK package upgrade.
After updating that empty-workload set, the executable Linux architecture
preflight passed restore/build and 45/45 tests; its SHA-256 is
`799A41E8181AA299E0E0E925E3F2818935F2F1842EA94FC66C1AC6E1D6EA7F0A`.
Both remain diagnostic only.

The R4 Linux candidate passed all 28 required rows and the complete 1,014-test
suite on commit `b19d982b76f2f54ebfb9f72e0f2aef85eb2632b8`. Its report and raw
Linux tiny-performance SHA-256 values are
`1346A080DFC7B397437F26088C16C074B9BA1AE3A4E3515D2E07D2DDAA7E2BDE`
and `2716C25672FDB332345059F83FAFDD0063E9CE8C6635B7FE198E9A4053FF6D41`.
The same-source R4 PR summary then failed with 1,013/1,014 tests because the
disposal race used a current-process lease-recovery override without its
required process-wide quiescence. The failed summary SHA-256 is
`7A3754C670A3622C569CB5E6AFFF479C9C668D20975E31641113337213FAD177`;
it emitted no `sync-probe.json`, and R4 nightly/release were not run. The
test-only correction uses normal concurrent recovery for leases and
reservations and avoids dereferencing borrowed spans during a disposal race.
The subsequent clean `009-r5-pre-final-pr` diagnostic passed all 24 gates and
1,014/1,014 tests on commit `527d451bd124dbbe8880fcb909b6e1bd70ad222a`.
R5 then passed Linux, PR, and nightly, but its release probe could not converge:
the harness imposed the 100-million-operation target on every Legacy
mixed-churn trial before reaching the LockFree qualification, and would have
done the same with the 100,000-frame Legacy ingest rows. Read-only liveness
sampling proved slow semaphore-serialized progress, not deadlock. R6 preserves
that attempt, keeps Legacy rows duration-bound, and applies count targets only
to LockFree with schema-v8/minimum-v8 per-run evidence. Its final pre-freeze
worktree passed 1,028/1,028 Release tests with zero skips, 9/9 completion-policy
cases, 5/5 watchdog race cases, both validation-only importers, documentation
validation, and a real four-row Legacy/LockFree duration/count smoke with zero
failures. Independent final review closed H0/M0 with no lower correctness
finding. All earlier results remain
diagnostic; only the R6 final paths linked above determine this conclusion.

Two still-earlier one-second smoke artifacts are also retained:

| Artifact | Shape | Failures | SHA-256 |
|---|---|---:|---|
| `short-matrix.json` | schema 3, both profiles, 7 scenarios, 54 run rows | 0 | `1EE8F7108912F0B440A72CCE14F2C5239159C5EF1AEDB3758E14802520C7F8DA` |
| `smoke-readers.json` | schema 2, both profiles, 2 reader scenarios, 12 run rows | 0 | `85F171400F3C642F81F22EFC66C452525A63F1985E49C45F8DB9B34BDFD62971` |

They identify commit `0cf7a43f9c39de1691b237a9761035339edd0964`,
predate later protocol corrections, and are smoke history only. The preserved
failed PR run at
`artifacts/lock-free-qualification/20260713T173242Z-pr/` has summary SHA-256
`436C4777B057472A448F0F925C3A16A778834977182ACF88C75E84C6B1F607E1`;
its `CorruptStore` result is the regression origin, not a final verdict.
