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
| PR | [`009-final-r3-pr/summary.json`](../../artifacts/lock-free-qualification/009-final-r3-pr/summary.json) and [`009-final-r3-pr/sync-probe.json`](../../artifacts/lock-free-qualification/009-final-r3-pr/sync-probe.json) |
| Nightly | [`009-final-r3-nightly/summary.json`](../../artifacts/lock-free-qualification/009-final-r3-nightly/summary.json) and [`009-final-r3-nightly/sync-probe.json`](../../artifacts/lock-free-qualification/009-final-r3-nightly/sync-probe.json) |
| Release | [`009-final-r3-release/summary.json`](../../artifacts/lock-free-qualification/009-final-r3-release/summary.json), [`009-final-r3-release/sync-probe.json`](../../artifacts/lock-free-qualification/009-final-r3-release/sync-probe.json), and the runner-created Windows [`009-final-r3-release/os-validation.json`](../../artifacts/lock-free-qualification/009-final-r3-release/os-validation.json) |
| Linux x64 | [`009-final-r3-linux-x64.json`](../../artifacts/lock-free-os-validation/009-final-r3-linux-x64.json) and its required raw [`linux-tiny-performance.json`](../../artifacts/lock-free-os-validation/009-final-r3-linux-x64.evidence/linux-tiny-performance.json) |

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
# Linux x64, explicitly inside Ubuntu on the same clean commit
wsl -d Ubuntu -- bash -lc 'cd /mnt/c/Users/rantr/source/repos/SharedMemoryStore && pwsh -NoProfile -File ./scripts/validate-lock-free-os.ps1 -Command all -Configuration Release -OutputPath artifacts/lock-free-os-validation/009-final-r3-linux-x64.json'

# Windows x64, on that same clean commit
pwsh ./scripts/run-lock-free-qualification.ps1 -Tier pr `
  -EvidenceRunId 009-final-r3-pr
pwsh ./scripts/run-lock-free-qualification.ps1 -Tier nightly `
  -EvidenceRunId 009-final-r3-nightly
pwsh ./scripts/run-lock-free-qualification.ps1 -Tier release `
  -EvidenceRunId 009-final-r3-release `
  -AdditionalOsEvidence artifacts/lock-free-os-validation/009-final-r3-linux-x64.json
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

## First final candidate and R2 convergence lineage

The first immutable final candidate is also preserved rather than rewritten.
Its Linux report, `artifacts/lock-free-os-validation/009-final-linux-x64.json`,
passed all required rows on commit `73accd7b33730027fed8da54d99d72239eeb1d59`,
tree `2d0deebeb5da5b9be578297fbaf7965079d3b63f`, and source-manifest SHA-256
`780D563964615DB8C5BF234D257F074737D1223EC98660E4FDA77D022C8235EF`.
The report SHA-256 is
`6C9727CCD6C412CD9B036E2107DA389F7359281A630C120BF48919FD987668B2`;
its raw performance report SHA-256 is
`EA05742E2CEC2F2A3032C18EADE888C0DDD026A8C08358AD1477B57BFC966CC7`.
The matching `009-final-pr` attempt then passed the complete 1,013-test suite
but correctly failed SC-017 because its generated configuration covered 46
directory mutations while the source catalog contained 50. Its summary SHA-256
is `E1B5FA0D0EF419388054B1AA310A292DB689ABBB7F33DDA5BD377166021CB79E`.
That failure rejects the entire first candidate despite the Linux pass.

The preserved R2 diagnostics then closed four distinct gaps. The
`009-r2-pre-final-pr` attempt exposed an obsolete 45-second parent timeout in a
raw mapped-memory atomic proof, not a store failure; isolated Windows and Linux
atomic reports passed with SHA-256
`B39F7970C718C8A4EDF384AFD3A196D29B8C4F8B91FF131C31DFF67910E74FD7`
and `51CC15DDB526B330801F4815BD44A330B22B6EBEF0E211898A09A293D742742E`.
The rejected parent-run summary SHA-256 is
`FC0949A5D7B56EFC7A5E707A3B8ED50C41738C2CD90A94264A88AA8E7BDF21E6`.
One separate full-solution observation found a test-only 50-millisecond setup
budget expiring before an instrumented reservation checkpoint under parallel
load. That observation emitted no named immutable diagnostic artifact or hash;
focused repetition closed it with a two-second budget and 2.25-second pause and
is deliberately recorded only as unbound test history.
`009-r2-pre-final2-pr` rejected two churn result rows where the contract requires
the one exact SC-016 method; its summary SHA-256 is
`3483EE66ED1DDA3E71864CC16D73C5569D195BF3C74AF800CDDCBD1CF044E0ED`.
`009-r2-pre-final3-pr` then passed 104/108 checkpoint/workload rows and isolated
checkpoint 62 oracle handling and checkpoint 63 finite-budget ownership in both
suspension workloads. Its summary SHA-256 is
`48F632E637B2999AFD877FCCA094322D6C9CA029903E495CF32D80C43A9FC66A`.
The former was corrected in the crash command; the latter required a production
reclaimer change so an expired remover cannot claim `Reclaiming` after
suspension and leave the key unhelpable.

Finally, the clean diagnostic `009-r2-pre-final4-pr` passed all 24 qualification
rows on commit `ca200238423877044d841a3b92f93edb37385d46`, tree
`5f4454fa25bbfb67ea0e687153ef94470ca11112`, and source-manifest SHA-256
`952FB17E4D08B5C4973A0809F0FC3541E5409B32991A07C6416709CB6C0CD5BF`.
It passed 1,014/1,014 tests, the single exact 10,000-cycle churn test, all 50
SC-017 configurations, and 108/108 suspension rows across 54 checkpoints and
two workloads. Its summary, synchronization probe, and suspension SHA-256 values
are respectively
`3D543FBF5E4C4A5C514C1C3309565F0B2575126E5B176B79E347802AC46E331A`,
`9788BF7222BFE8B77E4512EECFEE9788BD1D898763AABBA0417F16D2AF8DE3EC`,
and `8BC2FD8F2BBFA288EE67610E43155186E5A445443059586BF1AEC34F8C7DAA35`.
All R2 artifacts named in this section are diagnostic lineage only. They do not
replace the frozen common-provenance final sequence linked above.

## Rejected R2 final invocation

The second candidate is preserved at
`artifacts/lock-free-os-validation/009-final-r2-linux-x64.json`. It is schema 3
`not-qualified` with report SHA-256
`090651C119CADD7DAC2C545D04F595FD9E65F5CEFAFC1B9B79EDA009F66EAA7C`
and clean provenance for commit `00c0dda2f3412bdba0faac487cc5ab5596ced7fb`,
tree `a960691095f5a555aa95869eeb233d561231d64d`, and source-manifest SHA-256
`92BAA2E3DF0BE60BFB3FF314821A98ECAE3F3BCA730CA36BC008E46D3C03D981`.
The command was mistakenly launched by Windows PowerShell, so the report
correctly identified a Windows host: completed architecture, atomic, raw,
no-lock, crash, Release-test, Docker, sample, and pack rows passed, while the
required native and Python rows rejected the missing Windows `cmake` and the
Linux-only tiny-performance/`strace`/SIGSTOP rows were optional and not
applicable. The report therefore cannot satisfy the intended Linux contract.
This is an operator-platform failure, not a product or completed-test failure,
and it invalidates the entire R2 candidate.

Before the R3 freeze, the explicit Ubuntu login-shell command above passed
validation-only orchestration as Linux x64 with every structural self-test
passed. That diagnostic report is
`artifacts/lock-free-os-validation/009-r3-linux-invocation-validate.json`, SHA-256
`AE16F3AED1A9E0FE5113F20AD3AB2F28AE194E5CF0717F6372BF87362A51666C`.
It proves correct platform selection and orchestration only; it is not final
qualification evidence.
