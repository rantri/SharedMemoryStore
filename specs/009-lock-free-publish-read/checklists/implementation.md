# Implementation Evidence Checklist

## Conditional interpretation

This checklist is frozen before final execution. A checked statement means its
machine predicate is defined and is true **if and only if** the linked JSON
reports the required pass on one identical clean commit/source manifest. It is
not a claim that an artifact already exists. Missing, failed, not-qualified,
validation-only, skipped, dirty, stale, or provenance-mismatched evidence makes
the corresponding statement false and the release **NOT QUALIFIED**. The raw
ignored artifacts are authoritative; no tracked post-run checkbox edit is
allowed. See the complete predicate and SC mapping in
[`release-qualification.md`](../release-qualification.md).

- [x] PR is conditionally passed iff
  [`009-final-pr/summary.json`](../../../artifacts/lock-free-qualification/009-final-pr/summary.json)
  is schema 4 `passed` and its hashed
  [`sync-probe.json`](../../../artifacts/lock-free-qualification/009-final-pr/sync-probe.json)
  passes exact short-matrix, correctness, and provenance validation.
- [x] Nightly is conditionally passed iff
  [`009-final-nightly/summary.json`](../../../artifacts/lock-free-qualification/009-final-nightly/summary.json)
  is schema 4 `passed`, exact configured counts pass, and its
  [`sync-probe.json`](../../../artifacts/lock-free-qualification/009-final-nightly/sync-probe.json)
  is hash-bound to the same clean source.
- [x] Release is conditionally passed iff
  [`009-final-release/summary.json`](../../../artifacts/lock-free-qualification/009-final-release/summary.json)
  is schema 4 `passed` and exact long counts, three 60-second trials, waits,
  recovery, churn, race, allocation, copy, and threshold gates all pass.
- [x] Windows x64 is conditionally passed iff the runner-created
  [`os-validation.json`](../../../artifacts/lock-free-qualification/009-final-release/os-validation.json)
  is schema 3 `pass`, qualified x64, and every required row passes.
- [x] Linux x64 is conditionally passed iff
  [`009-final-linux-x64.json`](../../../artifacts/lock-free-os-validation/009-final-linux-x64.json)
  is schema 3 `pass`, qualified x64, every required row passes, and its required
  [`linux-tiny-performance.json`](../../../artifacts/lock-free-os-validation/009-final-linux-x64.evidence/linux-tiny-performance.json)
  is schema 6 with the exact 2-profile/2-scenario/8-process/3-trial release
  matrix, zero failures, `[0,63]` mask-valid complete affinity, coherent paired
  successes per completed cycle, no checksum/corruption evidence, reproducible
  medians, both Linux no-regression ratios, and every raw lock-free stall at
  most 10 ms.
- [x] Cross-platform provenance is conditionally passed iff PR, nightly,
  release, both OS reports, every sync probe, and start/completion assembly
  manifests identify the identical clean commit, tree, status hash, and source-
  manifest hash.
- [x] SC-001 through SC-018 are conditionally passed iff every named field/gate
  in the release qualification mapping passes; no averaging or narrative
  substitution is permitted.
- [x] Full tests and compatibility are conditionally passed iff full-solution
  TRX has zero non-passed tests and required package, native, Python, Docker,
  samples, release-tests, and pack rows all pass.
- [x] Leak freedom and bounded waits are conditionally passed iff all three
  executable owner/leak mappings pass and every selected wait/cancellation case
  completes within its limit plus 250 ms.
- [x] Review is conditionally accepted iff `code-review.md` has no unresolved
  high-severity invariant for the identical committed source proven by release
  JSON.
- [x] Evidence immutability is conditionally passed iff the fixed paths were
  absent before execution, scripts did not overwrite evidence, evidence
  manifests are the exact normalized non-reparse file sets below their sibling
  `.evidence` roots, every command log and raw performance file is hash-bound,
  accepted OS report/tree digests revalidate at runner completion, and tracked
  files were not edited during or after the qualifying sequence.

Historical Windows/Linux tests, bounded SC-011 races/generated histories, and
native/Python runs are explicitly diagnostic for their producing snapshots.
Their stale counts are not current-tree claims and they do not make any
conditional checkbox true by themselves.
