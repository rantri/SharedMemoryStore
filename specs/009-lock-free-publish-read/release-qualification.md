# Lock-Free Release Qualification

## Frozen one-shot qualification contract

This tracked document is frozen before the final runs. It does not narratively
declare a result and must not be edited after a run to turn a failure into a
pass. The release is **QUALIFIED if and only if** every predicate below is true
in the linked raw JSON; otherwise it is **NOT QUALIFIED**. The raw JSON below is
the authoritative final record, even when it reports `failed`,
`not-qualified`, or is absent.

| Tier or platform | Authoritative raw evidence |
|---|---|
| PR | [`009-final-pr/summary.json`](../../artifacts/lock-free-qualification/009-final-pr/summary.json) and [`009-final-pr/sync-probe.json`](../../artifacts/lock-free-qualification/009-final-pr/sync-probe.json) |
| Nightly | [`009-final-nightly/summary.json`](../../artifacts/lock-free-qualification/009-final-nightly/summary.json) and [`009-final-nightly/sync-probe.json`](../../artifacts/lock-free-qualification/009-final-nightly/sync-probe.json) |
| Release | [`009-final-release/summary.json`](../../artifacts/lock-free-qualification/009-final-release/summary.json), [`009-final-release/sync-probe.json`](../../artifacts/lock-free-qualification/009-final-release/sync-probe.json), and the runner-created Windows [`009-final-release/os-validation.json`](../../artifacts/lock-free-qualification/009-final-release/os-validation.json) |
| Linux x64 | [`009-final-linux-x64.json`](../../artifacts/lock-free-os-validation/009-final-linux-x64.json) and its required raw [`linux-tiny-performance.json`](../../artifacts/lock-free-os-validation/009-final-linux-x64.evidence/linux-tiny-performance.json) |

The predicate is:

1. The three summaries have schema 4, `validationOnly: false`, their exact
   `pr`, `nightly`, and `release` tiers, and `overallStatus: passed`; every
   required result is passed and neither skips performance nor OS validation.
2. Every summary has `provenance.workingTreeState: clean`; its start and
   completion `commit`, `headTree`, `workingTreeState`, `statusSha256`, and
   `sourceManifestSha256` are equal; its tested-assembly manifests are equal;
   and its `completion-integrity` result is passed.
3. All three summaries identify one identical clean commit, tree, status hash,
   and source-manifest hash. Their evidence manifests reproduce the hashes of
   the linked `sync-probe.json` files and all subsidiary logs/TRX files.
4. The release summary and both OS reports identify that same provenance. The
   Windows and Linux reports have schema 3, `validationOnly: false`,
   `qualifiedArchitecture: true`, `overallStatus: pass`, and every required
   row passed. The release summary's `dual-platform-os-evidence` result is
   passed. Optional rows may be `not-qualified` only when `required: false`.
   Each OS report's manifest is the exact file set below its sibling `.evidence`
   directory: paths are unique, normalized, in-root and non-reparse; every
   length/hash and executable-row stdout/stderr binding matches the file on
   disk. The release summary records each accepted OS report hash/tree digest
   and revalidates both at completion.
5. The release `sync-probe.json` has executable schema 7, exact configured
   rows/trials/counts, matching source and tested-assembly provenance, zero
   correctness failures, and every threshold accepted by the runner. The
   independent review in `code-review.md` has no unresolved high-severity
   finding for the same committed source. The Linux OS report additionally has
   one required `linux-tiny-performance` row and the Windows OS report has the
   same row as optional/not-qualified. The Linux row binds schema-7 raw JSON for
   exactly Legacy/LockFree x acquire-release/publish-remove x process-counts 1
   and 8 x three 60-second trials after 10 seconds of warm-up, with complete affinity,
   unique native CPU IDs in `[0,63]`, zero failures, at least two operations and
   exactly one successful operation pair per completed cycle, no checksum or
   corruption evidence, and reproducible summaries. For each scenario,
   one-process lock-free/legacy p99 is at most 1.0, eight-process lock-free/
   legacy throughput is at least 1.0, lock-free eight/one-process p99 is at most
   3.0, lock-free eight-process p99 is at most 10 microseconds, and every raw
   lock-free `MaxMicroseconds` is at most 10,000.

If any predicate is false, any command exits nonzero, or any artifact path
already exists before its one shot, the freeze is invalid. Preserve the failed
artifacts, revise the implementation or this pre-run contract as appropriate,
commit a new clean tree, choose new immutable paths, and rerun the complete
sequence. Never repair or copy JSON by hand and never edit tracked evidence
bookkeeping after the qualifying run.

## Exact execution sequence

Run from the clean commit. The ignored `artifacts/` tree retains authoritative
raw evidence without changing repository provenance.

```powershell
# Linux x64, on the same clean commit
pwsh ./scripts/validate-lock-free-os.ps1 -Command all -Configuration Release `
  -OutputPath artifacts/lock-free-os-validation/009-final-linux-x64.json

# Windows x64, on that same clean commit
pwsh ./scripts/run-lock-free-qualification.ps1 -Tier pr `
  -EvidenceRunId 009-final-pr
pwsh ./scripts/run-lock-free-qualification.ps1 -Tier nightly `
  -EvidenceRunId 009-final-nightly
pwsh ./scripts/run-lock-free-qualification.ps1 -Tier release `
  -EvidenceRunId 009-final-release `
  -AdditionalOsEvidence artifacts/lock-free-os-validation/009-final-linux-x64.json
```

The configured release run is the exact 100,000,000-operation mixed workload,
1,000,000 production repetitions per SC-011 family, 1,000,000 directory-
generation repetitions, 10,000 recovery cases, three 60-second trials after a
10-second warm-up, 100,000 direct 1.3 MB frames, and 30-second suspension gate.
The Linux `-Command all` run also executes the independently validated one/eight-
process tiny-operation matrix described above and preserves its raw JSON inside the
Linux OS evidence tree.
Exit code `0` is necessary but not sufficient: the JSON predicate above is the
final authority. Exit code `1` is failure. Exit code `2`, `validation-only`, a
missing prerequisite, a skipped gate, or an unsupported environment is
not-qualified.

## Success-criterion evidence map

Every row below is conditional: **PASS** means the named machine gate is passed
in the final release JSON with the exact clean provenance above. No prose can
override a failing or missing field.

| Criterion | Final machine-derived gate |
|---|---|
| SC-001 | Release `sync-probe` mixed-churn row has at least 100,000,000 operations and zero correctness failures; `raw-visibility`, `churn`, and `owner-leak-assertions` are passed. |
| SC-002 | Release `sync-probe` same-key-read 6/1 and 12/1 median throughput assertions are at least 4x and 7x. |
| SC-003 | Release `sync-probe` distributed-key-read 6/1 and 12/1 median throughput assertions are at least 4.5x and 8x. |
| SC-004 | Release `sync-probe` broker-directed 12-reader publication rate is at least 80% of its one-reader rate. |
| SC-005 | Release `participant-suspension` is `passed` with `qualification: sc005-qualified`, every configured checkpoint, 30-second pauses, and healthy-throughput ratio at least 0.9. |
| SC-006 | Release `sync-probe` 8-process Windows tiny-operation throughput/p99 assertions pass; Linux `linux-tiny-performance` binds the exact one/eight-process three-trial raw matrix and passes uncontended p99, eight-process throughput, <=3x self-amplification, <=10 us absolute p99, and every-raw-trial 10 ms maximum-stall gates for both scenarios; `dual-platform-os-evidence` and completion OS-tree revalidation pass. |
| SC-007 | Release `wait-policy` includes the no-operation-lock proof; Windows `no-lock-held` and Linux `no-lock-held` plus required `no-lock-linux-strace` OS rows pass. |
| SC-008 | Release `sync-probe` exact allocation scope reports `ProducerStoreOperationAllocatedBytes == 0` and the full-suite allocation tests pass for warmed publish/reserve/commit and acquire/project/release paths. |
| SC-009 | Every release large-ingest row reaches 100,000 frames, reports zero producer store-operation allocation, and carries `structural-direct-reservation-write-and-borrowed-lease-read` copy evidence with zero correctness failures. |
| SC-010 | Release `recovery` proves exactly 10,000 cases and full capacity, `owner-leak-assertions` passes, and both OS reports' required crash/checkpoint rows pass. |
| SC-011 | Release `production-race-stress` has exactly one valid marker for each of eight families with 1,000,000 production races; `production-generated-histories` and `reference-model-histories` exact family/count gates also pass. |
| SC-012 | Release `wait-policy` TRX passes the full wait/cancellation matrix with `completionAllowanceMilliseconds=250`; all three `owner-leak-assertions` mappings pass. |
| SC-013 | Release `full-test-suite`, `unit-contract`, `contract`, `package-consumption`, build, and both OS reports' required `release-tests`, native, Python, Docker, and pack rows pass. |
| SC-014 | Release full-suite sample validation passes and each OS report's required `sample-6` and `sample-12` rows passes. |
| SC-015 | The release full-suite TRX passes the barrier-controlled 12-process lease/removal/reclamation integration test with zero non-passed tests. |
| SC-016 | Release `churn` proves the configured lifecycle count and final capacity; every mixed-churn trial has zero correctness failures and late/early p99 at most 2.0. |
| SC-017 | Release `directory-generation-stress` is `passed` with `qualification: sc017-qualified-count-and-correctness` and exactly 1,000,000 repetitions across all configured mutation checkpoints. |
| SC-018 | All three release sticky-overflow-miss trials meet the exact 4,096-slot/10,000-cycle/16,384-sample shape, observe real spill and cleanup, drain occupancy, add zero late scans, have zero failures, and report late/early p99 at most 2.0. |

## Diagnostic evidence available before the freeze

Historical development runs include Windows release tests, bounded SC-011
production races and generated histories, native/Python checks, and focused
Linux tests. Their counts belong to the source snapshots that produced them and
are intentionally not restated as current-tree totals. They are useful
regression evidence only: they are not at the final immutable paths, do not
jointly prove the final clean source manifest, and satisfy none of the
conditional release rows above.

## Preserved historical failure

The failed PR attempt at
`artifacts/lock-free-qualification/20260713T173242Z-pr/` remains intentionally
preserved. Its `summary.json` SHA-256 is
`436C4777B057472A448F0F925C3A16A778834977182ACF88C75E84C6B1F607E1`.
Its ordinary eight-process publish/remove workload returned `CorruptStore`.
That artifact predates exact-reference revalidation and is failure evidence,
not evidence for the final tree.
