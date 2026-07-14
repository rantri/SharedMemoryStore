# Short Convergence Report

## Frozen conditional conclusion

This report is frozen before the one-shot runs and is not edited afterward.
The short convergence conclusion is **PASS if and only if** the final PR
[`summary.json`](../../../artifacts/lock-free-qualification/009-final-pr/summary.json)
has schema 4, tier `pr`, `validationOnly: false`, and `overallStatus: passed`,
and its hashed
[`sync-probe.json`](../../../artifacts/lock-free-qualification/009-final-pr/sync-probe.json)
passes every exact row, count, provenance, correctness, raw-visibility, and
short-performance check enforced by the runner. Its start/completion provenance
must be identical and clean, and must match the final nightly and release
summaries. Otherwise the short conclusion is **FAIL** or **NOT QUALIFIED** as
reported by the raw JSON; this tracked report cannot promote it.

The same clean commit then has to pass the immutable
[`009-final-nightly`](../../../artifacts/lock-free-qualification/009-final-nightly/summary.json)
and
[`009-final-release`](../../../artifacts/lock-free-qualification/009-final-release/summary.json)
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
