# Final independent concurrency and code review

**Feature**: `009-lock-free-publish-read`

**Branch**: `codex/lock-free-csharp`

**Review date**: 2026-07-14

**Decision**: **Code and qualification harness approved; final release evidence pending**

## Verdict

The tracked implementation is approved on code, protocol, focused-test, and
qualification-harness review grounds. The final independent re-review found no
unresolved High or Medium issue in the production implementation or the release
harness. This is not yet release approval: every immutable artifact and
provenance condition in [Final evidence gate](#final-evidence-gate) must still
pass on one identical clean source state. A missing, failed, provenance-mismatched,
or incomplete gate leaves the feature unapproved for release.

## Review closure

The final independent review covered public compatibility, atomic/control-word
encodings, directory and slot generations, participant incarnation and
recovery, helping and progress, operation budgets, disposal, Linux lifecycle
artifacts, PID namespaces, zero-allocation paths, production race oracles, and
qualification harnesses.

- Original High findings `CR-H01` through `CR-H10`: **resolved**.
- Original Medium findings `CR-M01` through `CR-M08`: **resolved**.
- SC-011 production-race and generated-history re-review: **approved**, with no
  High or Medium finding.
- Exact Linux PID-namespace publication, classification, and mixed-namespace
  recovery rules: **reviewed clean**.
- Linux owner-anchor cleanup and orphan sweep: **reviewed clean** after the
  FIFO-safe/statx artifact-classification correction.
- Final directory/recovery hardening: **reviewed clean** after exact CAS-loss
  cleanup validation, stable reservation-metadata tuple validation, and the
  legal canceling zero-location window were covered by deterministic tests.
- Final lifecycle successor hardening: **reviewed clean** after lease,
  participant, remove/reclaim, and abort/reclaim transitions rejected stable
  same-generation regressions while accepting exact or later-incarnation
  successors and transient movement.
- Historical Windows Release and focused Linux owner-anchor, PID-namespace, and
  profile-open sets passed on their producing snapshots; stale counts are not
  asserted for the current tree.
- Qualification-harness remediation: **independently reviewed clean**, with no
  unresolved High or Medium finding. The review covered the exact raw Linux
  tiny matrix, every-trial stall gate, mask-valid sparse affinity,
  paired-success/checksum/corruption coherence, exact metric tuples,
  manifest/file-set/log/path binding, reparse rejection, and completion
  revalidation.

### Cold-open initialization-authority closure

A final structural review found a High-severity cold-path race: an opener could
map an already-created zeroed region before acquiring cross-process lifecycle
coordination, infer initialization authority from its open mode, and overwrite
the physical creator's eventual header. This did not put an OS lock on any hot
operation, but it made concurrent create/open unsafe.

The closed implementation now treats physical creation as the only
initialization authority. `SharedStoreOpenScope` owns the ordered cold
transaction and carries an explicit `CreatedNew`/`OpenedExisting` disposition
through header initialization or validation and participant registration. The
Windows named gate is acquired before mapping. Linux acquires `.lifecycle`,
reconciles and removes only proven-stale artifacts, then acquires `.lock`
before mapping, owner publication, and engine construction. The original
wait/cancellation budget covers the whole transaction, and failure releases the
ordered gates before mapped-owner cleanup can re-enter lifecycle coordination.

The profile/open matrix now proves that every legacy/lock-free and open-mode
combination preserves an existing zero header, that a held cold scope blocks
before any physical mapping or owner publication, that an opposite-profile
sentinel is preserved, and that cleanup transfers/disposes ownership exactly
once. Focused Linux owner-marker, profile-open, and wait-policy tests passed.
Independent re-review found no unresolved High, Medium, or concrete Low issue.

### Directory handoff and delayed-helper closure

The first full Linux tiny-operation diagnostic on commit `d834146` is preserved
at `artifacts/lock-free-os-validation/009-pre-final-linux-x64.json`. Acquire and
release passed, and the first publish/remove trial passed, but later trials
latched corruption and then recorded exactly 5,379,795,214
`Publish.CorruptStore` results. The first traces led to
`LockFreeKeyDirectory.HelpMutation` and `TryPublishExactLocation`. Root-cause
analysis showed that delayed helpers treated legal cancellation and
same-generation location handoffs as a stable malformed store.

The corrected protocol revalidates the canonical mutation, operation, location,
slot control, immutable directory binding, and relevant target cells as one
joint tuple before latching corruption. A stable invalid tuple requires two
identical collections plus exact no-op CAS confirmation. Canceling Insert
handoffs, first-publisher arbitration for `Unlink/Prepared`, valid alternate
locations after `Unlink/TargetSelected`, post-CAS withdrawal, old residue, and
future-generation observations now have explicit generation-fenced outcomes.
Malformed stable tuples remain fail-closed. Checkpoints 66 and 67 cover the two
new publication/revalidation windows and extend the canonical catalog to 67.

Deterministic unit schedules cover exact, empty, valid replacement, malformed,
out-of-range, alternate-location, post-CAS, and real-reuse outcomes. The
canonical crash matrix passed 67/67 on Windows x64 and Linux x64; the final
8-process churn regression passed on both platforms. A longer pre-correction
cross-platform churn run contributed 498,742,938 public API calls with no
failure, and the final focused runs remained clean after the adjacent fixes.
Independent re-review found no High or Medium issue. It retained one
non-blocking Low observation: a crash immediately after an old-generation
zero-to-tagged location CAS may leave non-visible generation-fenced residue
until recovery or reuse cleanup; that residue cannot project bytes or overwrite
a future nonzero word.

### Current pre-qualification aggregate

On the resulting unchanged worktree, both Windows x64 and Linux x64 Release
aggregates passed with zero skips: solution build 0 warnings/0 errors, Unit
415/415, Contract 117/117, Integration 295/295, Interop 75/75, and
Linearizability 83/83, for 985/985 tests per platform. Documentation validation,
`git diff --check`, and qualification `ValidateOnly` also passed on both
platforms. These are pre-qualification results and do not replace the immutable
final evidence gate.

### Projection lifetime race closure

The final projection re-review found and closed the raw-visibility failure in
which an exact active lease could read `DescriptorLength` successfully and then
receive an empty `DescriptorSpan`. Each property had performed a separate
preflight plus projection validation, and a legal
`Published(g) -> RemoveRequested(g)` transition between the validator's slot
control reads was treated as an invalid snapshot even though the active lease
still protected immutable metadata and bytes.

The closed implementation now:

- retries the bounded projection snapshot across that single legal forward
  transition;
- re-proves the exact Active lease and a stable slot control before terminally
  latching an impossible lifecycle or invalid mapped metadata;
- treats a copied-token release/reclaim/reuse race as benign lease expiry rather
  than mapped corruption;
- keeps `ValueLease` profile-neutral and one-pass by making both engines own
  invalid-token projection semantics, including legacy post-release guards; and
- appended checkpoint 65 for the metadata/control revalidation window, routed it
  through the crash agent and canonical catalog, raises the PR recovery count to
  65, and added non-Participant/non-Disposal checkpoint IDs 62, 63, and 65 to the
  exact suspension configuration.

Independent reviewer executions on the resulting worktree passed:

- 4/4 selected deterministic projection, copied-release, lifecycle-corruption,
  and mapped-metadata-corruption unit cases;
- 9/9 legacy and lock-free lease contract cases; and
- 2/2 cross-process raw-visibility integration cases after the final one-pass
  getter change.

At that intermediate projection-review stage, focused implementation validation
completed the then-current 65/65 canonical checkpoint crash catalog. The later
directory closure above extends and revalidates the current catalog at 67/67.
These worktree results close the projection review with **no unresolved High or
Medium finding**; they do not claim or replace the immutable final release
evidence.

The focused results above establish closure of the review findings; they are not
a substitute for the immutable final qualification artifacts below.

## Accepted design boundaries

These are intentional contract boundaries, not open findings:

- The product remains a bounded shared-memory **key-value store**, not a stream,
  queue, broker, or worker scheduler. External brokers distribute keys; any
  publisher, worker, observer, or other process may independently access the
  store.
- Layout 2.0 steady-state publish, reserve, commit, acquire, release, remove,
  reclaim, and recovery paths do not acquire a named process-wide semaphore.
  OS-backed serialization is confined to bounded cold lifecycle coordination.
- Layout 1.2 remains the default legacy profile. Layout 2.0 is explicit,
  fail-closed, x64-only in this release, and has no in-place conversion path.
- Participant-table exhaustion remains explicit. Open returns
  `ParticipantTableFull` and performs no hidden recovery or liveness scan.
- Recovery is cooperative and caller initiated; there is no hidden maintenance
  thread. Ordinary callers may help published owner-free transitions without
  reclaiming resources from a live owner.
- Current-process lease or reservation recovery overrides are administrative,
  process-wide quiescent operations. Normal concurrent recovery uses the safe
  non-override mode.
- Linux PID identity is interpreted only in its exact namespace. Once an opener
  cannot prove same-namespace operation, the store irreversibly enters mixed
  mode and preserves ambiguous live/registering ownership conservatively.
- Linux owner anchors are cold-lifecycle liveness evidence, not operation locks.
  Cleanup deletes only well-formed, unreferenced, provably unlocked artifacts;
  locked, ambiguous, malformed, and special-file entries are retained
  conservatively.

## Final evidence gate

All of the following prelinked artifacts must exist and report success:

1. [artifacts/lock-free-qualification/009-final-pr/summary.json](../../artifacts/lock-free-qualification/009-final-pr/summary.json)
2. [artifacts/lock-free-qualification/009-final-nightly/summary.json](../../artifacts/lock-free-qualification/009-final-nightly/summary.json)
3. [artifacts/lock-free-qualification/009-final-release/summary.json](../../artifacts/lock-free-qualification/009-final-release/summary.json)
4. [artifacts/lock-free-qualification/009-final-release/sync-probe.json](../../artifacts/lock-free-qualification/009-final-release/sync-probe.json)
5. [artifacts/lock-free-qualification/009-final-release/os-validation.json](../../artifacts/lock-free-qualification/009-final-release/os-validation.json)
6. [artifacts/lock-free-os-validation/009-final-linux-x64.json](../../artifacts/lock-free-os-validation/009-final-linux-x64.json)
7. [artifacts/lock-free-os-validation/009-final-linux-x64.evidence/linux-tiny-performance.json](../../artifacts/lock-free-os-validation/009-final-linux-x64.evidence/linux-tiny-performance.json)

Approval requires the machine-checkable evidence to show that:

- the PR, nightly, and release profiles completed with their required checks
  passed;
- the release synchronization probe and Windows OS validation passed;
- the Linux x64 OS validation passed its required checks, including the exact
  schema-6 8-process tiny-operation raw matrix, median no-regression ratios, and
  every-lock-free-trial 10 ms stall ceiling;
- both exact sibling OS evidence trees, manifests, executable logs, and accepted
  report/tree digests passed initial and completion validation;
- every artifact identifies the same clean commit and identical source manifest;
- the independent qualification-harness and production-code re-reviews have no
  unresolved High or Medium finding; and
- the tracked tree was not edited after evidence collection.

No final release-evidence status, commit hash, source-manifest hash, or artifact
hash is asserted in this review before those files are produced. If the seven
artifacts satisfy the conditions above, the release decision is **final
approved**. Otherwise the code and harness review remains closed, but the
feature is **not approved for release** until the failing gate is rerun on one
identical clean source state.
