# Final independent concurrency and code review

**Feature**: `009-lock-free-publish-read`

**Branch**: `codex/lock-free-csharp`

**Review date**: 2026-07-15

**Decision**: **Code approved; immutable final release evidence pending**

## Verdict

The tracked implementation is approved on code, protocol, focused-test, and
pre-final qualification grounds. Independent re-review of the reclamation,
cold-open, Linux record-lock lifetime, benchmark, and evidence-importer deltas
found no unresolved High or Medium issue in the production implementation or
release harness. R5 passed Linux, PR, and nightly on one clean source state but
its release workload was rejected as non-convergent because the harness applied
lock-free durability counts to the semaphore-serialized Legacy comparison rows.
R6 corrects only the benchmark completion/evidence policy, preserves the full
store matrix, and adds a hard monotonic deadline around duration-bound trials;
it does not change production store code. This is not yet release approval:
every immutable artifact and
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
- Linux record-lock lifetime, load-context/native same-PID exclusion, stable
  pathname identity, and teardown ordering: remediated with OFD locks,
  persistent rendezvous inodes, descriptor-before-region cleanup, and focused
  same/foreign-process regressions; final independent re-review is recorded
  below.
- Pre-final evidence recomputation: **reviewed clean** after both PowerShell
  median helpers adopted explicit midpoint-floor semantics and distinct
  odd/even self-tests; the real three-trial raw report recomputes exactly.
- R4 disposal-test correction: **reviewed clean**. The concurrent theory now
  uses normal non-override lease/reservation recovery and inspects only borrowed
  span shape after racing disposal; exact content remains covered on the live
  second handle and in dedicated data-path suites. No production code changed.
- R6 completion-policy and convergence correction: **reviewed clean**. Config
  schema 5 selects only `LockFree` as count-bound; probe schema 8/minimum 8
  records the selected profiles and exact per-run targets. Legacy mixed-churn
  and large-ingest remain three duration-bound comparisons, while every
  LockFree mixed/large trial retains its 100,000,000-operation/100,000-frame
  durability target. An atomic monotonic-deadline watchdog spans setup through
  final cleanup, rejects delayed timer dispatch, bounds child-kill effort, and
  then unconditionally fail-fast terminates the isolated probe on timeout.
- R6 independent re-review: **no unresolved High or Medium finding** after the
  initial watchdog, cleanup, schema-compatibility, identity, and exact-grace
  findings were corrected. Target-policy and watchdog tests, both validation-
  only importers, documentation validation, and a real four-row Legacy/LockFree
  duration/count smoke passed on the final pre-freeze worktree. The complete
  Release solution passed 1,028/1,028 tests with zero skips: Contract 117,
  Linearizability 83, Interop 75, Unit 451, and Integration 302.

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

### Linux OFD-lock, inode, and teardown closure

The first Linux audit found that one descriptor per wrapper was unsafe with
traditional process-associated `F_SETLK`: same-PID contenders did not conflict,
and closing any sibling descriptor could release a live lock. A shared managed
descriptor registry fixed that case inside one loaded assembly, but final review
found two further High-severity holes. The registry was static per
`AssemblyLoadContext`, not per OS process, so another package copy or native
module could bypass it. Separately, store and failed-open teardown disposed the
mapped region before the synchronization descriptor; final-owner cleanup could
unlink `.lock`, a same-process reopen could reuse the old descriptor/inode, and
a foreign process could create and lock a replacement inode.

Current C# and C++ adapters now use direct nonblocking `F_OFD_SETLK` calls.
Each C# wrapper owns one descriptor and a non-reentrant local gate. C++ wrappers
inside one module share their existing per-path `FileState`/descriptor and timed
mutex; distinct modules/load contexts/descriptors contend in the kernel.
Unsupported commands/filesystems fail closed, and unlock failure closes/retires
the affected descriptor before its local gate is reopened. Current cleanup retains `.lock`
as an empty mode-`0600` stable rendezvous, matching `.lifecycle`. Lock-free,
legacy, initialized, uninitialized, platform-failure, and failed-scope teardown
all dispose ordinary synchronization before region/owner cleanup may re-enter
`.lifecycle`; the native store uses the same order.

The accepted compatibility boundary is explicit: traditional and OFD locks
remain mutually exclusive across processes, and current managed/native OFD
implementations coexist inside one PID. Concurrently mixing an older
process-associated-`F_SETLK` package with a current adapter inside the same PID
is unsupported because closing any sibling descriptor can invalidate the old
lock.

Focused Linux evidence covers both lock paths, same-thread and timed contenders,
foreign exclusion, two independently loaded current assemblies, a same-PID
native-style OFD descriptor in both directions, and concurrent final close/reopen.
The last test locks the persistent pathname and proves the reopened legacy
store's actual operation returns `StoreBusy`, detecting the former inode split;
failed-open event recording proves synchronization disposal precedes owner
cleanup. A held gate for one store still does not block a different store name.
Final re-review also found that both adapters cleared their wrapper-local held
flag after waking the next local waiter; the old releaser could overwrite the
new holder's state and strand the local semaphore/mutex. Both now clear private
ownership before publishing the unlock, and a 4,000-handoff C# stress regression
proves exclusivity plus immediate wrapper reuse; the rebuilt native suite passes.

### Benchmark reproducibility-metadata closure

The final harness audit found one Medium evidence-contract gap: the benchmark
methodology required CPU model, logical/physical processor counts, total memory,
and exact store dimensions, but schema 6 omitted several fields and Linux could
record `processorIdentifier: unknown`. Both importers accepted any nonempty
identifier.

At that stage, schema 7 added portable processor model, logical/physical counts, total host
memory, and exact per-scenario slot/value/descriptor/key/lease/participant
dimensions. Windows uses processor-topology and physical-memory APIs; Linux uses
the process-visible CPU topology, `/proc/cpuinfo`, and `/proc/meminfo`. Detection
uncertainty emits an invalid zero/unknown value so qualification fails closed.
The then-current importers required schema 7, rejected missing/unknown/implausible metadata,
verified the exact selected scenario-dimension set, and included negative
self-tests for unknown CPU, missing memory, and dimension tampering. Focused
Windows/Linux builds, runtime smokes, and validator self-tests passed.

### Historical schema-7 post-review convergence evidence

After the OFD, teardown, persistent-inode, handoff, and schema-7 changes, fresh
Windows x64 and Linux x64 Release restores/builds completed with zero warnings
and zero errors. Each platform passed Unit 416/416, Contract 117/117,
Integration 302/302, Interop 75/75, and Linearizability 83/83: **993/993** with
no skips. The four additional integration cases are the load-context,
managed/native OFD, concurrent close/reopen, and shared-wrapper handoff
regressions.

A source-fresh Linux C++ build passed 5/5 native tests. With that exact native
library installed for Python and its exact C++ agent selected, the non-stress
C#/C++/Python interoperability matrix passed 65/65, including three-owner final
cleanup with persistent `.lock`. Documentation validation and `git diff --check`
passed. Independent final source/protocol/test review found no remaining High,
Medium, or Low issue in the Linux closure or deep lock-free core. These
worktree results establish code-review closure but do not replace the clean
same-commit immutable evidence gate.

Integration test classes are now serialized because several independent
classes each launch 8-12 subprocesses and their simultaneous execution measured
host-wide startup oversubscription against the one-second public cold-open
budget. Each scenario retains its real internal cross-process concurrency, and
the explicit two-store test preserves coverage that unrelated names remain
independent. The complete serialized Linux suite passed without increasing the
library timeout or weakening deadline checks.

A subsequent Linux-to-Windows clean-output switch exposed one unrelated sample
test dependency: `DockerSharedMemoryLocalSampleModeRuns` invokes the sample with
`--no-build`, but the integration project did not declare that sample as a
build-only reference. The integration project now carries the same
`ReferenceOutputAssembly=false` dependency pattern already used for the broker
sample and test agents. A fresh Windows solution restore/build then completed
with 0 warnings and 0 errors and the full aggregate passed 989/989.

### Remove classification and reclaim closure

`TryRemove` now performs one active-lease classification scan immediately
before claiming `RemoveRequested -> Reclaiming`; the reclaimer no longer repeats
the same full lease-table scan. A fresh budget check follows classification, so
NoWait, finite, infinite, cancellation, active-lease `RemovePending`, and
post-ordering reclaim outcomes retain their documented boundaries. Checkpoint
63 deterministically covers a lease acquired after the earlier observation and
before reclaim ownership. Focused remove/reclaim/wait validation passed 111/111,
and independent review found no unresolved High or Medium issue.

### Current pre-final convergence evidence

The production candidate `a99a656` passed complete Windows x64 and Linux x64
Release aggregates with no skips: Unit 416/416, Contract 117/117, Integration
298/298, Interop 75/75, and Linearizability 83/83, for **989/989** on each
platform. The later `50ea3a8` commit changes only the two evidence importers and
their self-tests; its clean Linux `-Command all` run repeated the full 989-test
Release aggregate and passed every required architecture, atomic, raw
visibility, held-lock/`strace`, 67-checkpoint crash, native, Python, Docker,
6/12-reader sample, and pack row. The optional container PID-namespace pause
row was `not-qualified` with `required: false`; the required Docker validation
passed.

The first clean long attempt, preserved as `009-pre-final3`, stopped after its
correct 24-run benchmark because both PowerShell importers converted the
three-value midpoint `1.5` to integer `2` and compared the C# median against the
maximum. All 96 stored producer medians matched correct recomputation and every
performance gate passed; later OS stages were correctly not run after the
integrity exception. Commit `50ea3a8` uses an explicit floored midpoint in both
importers and adds non-monotonic odd/even median self-tests. Windows and Linux
validation-only suites passed, the preserved raw report revalidated across five
cultures, and independent review found no High, Medium, or Low issue.

The fresh immutable diagnostic
`artifacts/lock-free-os-validation/009-pre-final4-linux-x64.json` then completed
with `overallStatus: pass`, start/completion clean provenance at `50ea3a8`, and
52 manifest-bound evidence files. Its exact performance summaries were:

| Profile | Scenario | Processes | Median calls/s | Median p99 | Maximum raw stall |
|---|---|---:|---:|---:|---:|
| Legacy | acquire/release | 1 | 314,130 | 9.5 us | 489.7 us |
| Legacy | acquire/release | 8 | 317,653 | 11.8 us | 131,729.1 us |
| LockFree | acquire/release | 1 | 3,975,778 | 0.7 us | 284.2 us |
| LockFree | acquire/release | 8 | 22,070,035 | 1.4 us | 4,017.0 us |
| Legacy | publish/remove | 1 | 1,168,006 | 2.3 us | 768.1 us |
| Legacy | publish/remove | 8 | 1,177,259 | 2.3 us | 151,389.6 us |
| LockFree | publish/remove | 1 | 1,430,035 | 1.8 us | 476.7 us |
| LockFree | publish/remove | 8 | 6,545,020 | 4.6 us | 2,460.9 us |

For both scenarios, lock-free one-process p99 beat legacy, eight-process
throughput beat legacy, eight/one lock-free p99 stayed below 3x, eight-process
p99 stayed below 10 us, and every lock-free raw stall stayed below 10 ms. This
is pre-final diagnostic evidence, not a substitute for the common-provenance
final gate.

### Terminal clean pre-final diagnostic

The remaining pre-final attempts were preserved rather than overwritten. In
order, they exposed and closed four release-evidence or bounded cold-start
defects: a WSL source-provenance timeout (`849a7a0` binds raw evidence to the
captured source tuple); empty strings emitted for non-executed OS fields
(`f754118` preserves JSON nulls); partially visible ready/go/done trace markers
(`5925fee` publishes each marker by same-directory atomic rename); and the
broker-key sample reusing the one-second default cold-open budget while starting
12 processes on an already loaded host (`853b10f` gives that sample operation a
single explicit ten-second budget). The marker correction passed 15 repeated
Linux `strace` gates and five complete Linux plus five Windows trace classes.
The sample correction passed 40 consecutive 12-worker Linux startups and fresh
6- and 12-worker Linux and Windows sample runs. Independent reviews of both
changes found no High, Medium, or Low finding; neither changes a layout-v2 hot
publish, acquire, release, or remove path.

One deliberately contaminated, concurrent diagnostic left 17,733 historical
Linux rendezvous files and demonstrated a scoped Low cold-path limitation:
release-marker and owner-anchor discovery scans the flat per-distro rendezvous
directory, so extreme high-cardinality store-name churn can consume a finite
cold-open budget. Stable `.lock` and `.lifecycle` inodes cannot be routinely
deleted without breaking lock identity. This does not introduce a global lock,
does not affect an already-open store, and is outside the intended stable-name
deployment; bounded `StoreBusy` remains the specified cold-path outcome. The
final diagnostics therefore used an idle, restarted WSL tmpfs. A future
resource-protocol revision should use per-store namespaces or direct marker
resolution and add a high-cardinality cold-open regression, without changing
the lock-free key-value engine.

After a Docker-integration-only not-qualified attempt was preserved as
`009-pre-final10`, the clean immutable diagnostic at
`artifacts/lock-free-os-validation/009-pre-final11-linux-x64.json` passed on
commit `853b10f317bcab16a4c69ead9b23b5bd6027ec7a`, tree
`69a8e453a3edc0528fab2a4a80fc151eff8820d0`, and source-manifest SHA-256
`90F772F9FA240CE44029DF02D2EFF5D294996E69CC9184473ED83D5ECD1E1194`.
Start and completion provenance and all 33 tested-assembly rows were identical.
All 28 required rows passed; the optional Docker-pause row was correctly
not-qualified with a JSON-null execution tuple. Unit 436/436, Contract 117/117,
Integration 302/302, Interop 75/75, and Linearizability 83/83 passed, for
**1,013/1,013** across five TRX files. Native C++, Python, required Docker,
6/12-worker samples, packing, held-lock/`strace`, all 67 crash checkpoints, and
SIGSTOP recovery also passed.

The report SHA-256 is
`28CD4C989898C481D26F77E8B1951771CFBAF1BC99ADDBC8358FBF09A66E5D27`;
its exact 52-file evidence-tree digest is
`FDA14EA9612EE0F891808FBA4348297CA766A72A4F32FBEDA541080EF8451478`;
and its raw schema-7 performance report SHA-256 is
`C621574937EA90DB110CF140641895F479E20425ACBF7B0BDFAB8BE3F2CA2CED`.
The actual release importer accepted the untouched report and recomputed the
same manifest and hashes. A separate audit independently reconstructed the
source digest from 589 blobs and found no evidence or importer finding.

| Profile | Scenario | Processes | Median calls/s | Median p99 | Maximum raw stall |
|---|---|---:|---:|---:|---:|
| Legacy | acquire/release | 1 | 251,281 | 13.5 us | 315.5 us |
| Legacy | acquire/release | 8 | 253,850 | 15.7 us | 111,495.7 us |
| LockFree | acquire/release | 1 | 3,983,372 | 0.7 us | 4,047.6 us |
| LockFree | acquire/release | 8 | 22,008,409 | 1.5 us | 4,023.2 us |
| Legacy | publish/remove | 1 | 970,910 | 2.7 us | 4,849.8 us |
| Legacy | publish/remove | 8 | 951,879 | 2.9 us | 111,292.2 us |
| LockFree | publish/remove | 1 | 1,425,052 | 1.8 us | 1,554.0 us |
| LockFree | publish/remove | 8 | 6,492,213 | 4.6 us | 2,363.8 us |

Acquire/release lock-free one-process p99 was 5.19% of legacy and its
eight-process throughput was 86.70x legacy; publish/remove was 66.67% and 6.82x
respectively. Lock-free eight/one-process p99 amplification was 2.14x and 2.56x.
Every contracted ratio, absolute p99, raw-stall, host-identity, affinity,
scenario-dimension, checksum, corruption, and exact-trial gate passed. This is
terminal diagnostic evidence and freezes T107/T108; it does not replace the
final PR/nightly/release and dual-platform common-provenance gate.

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

### Historical `844448e` pre-qualification aggregate

On commit `844448e` before the later reclamation/benchmark delta, both Windows
x64 and Linux x64 Release
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

### R2 qualification convergence and final re-review

The first immutable candidate remains preserved. Its Linux report passed on
`73accd7`, but the same-source `009-final-pr` summary correctly failed after
1,013/1,013 tests because SC-017 configured 46 directory mutations while the
source catalog contained 50. The Linux report SHA-256 is
`6C9727CCD6C412CD9B036E2107DA389F7359281A630C120BF48919FD987668B2`;
the failed PR summary SHA-256 is
`E1B5FA0D0EF419388054B1AA310A292DB689ABBB7F33DDA5BD377166021CB79E`.
Neither is promoted as final evidence.

R2 diagnostics subsequently rejected an obsolete atomic-test parent timeout,
ambiguous churn-test cardinality, and checkpoint 62/63 suspension boundaries.
The atomic implementation passed isolated Windows and Linux OS reports. The
qualification runner now binds churn to the one exact SC-016 method, binds each
leak assertion to its exact producing step, requires exactly one passed TRX row,
and verifies the configured top-level class plus its directly declared test
method. Its negative self-test covers role swaps, joint-mapping drift, nested
types/methods, wrong source, and wrapper/XML failure propagation. Checkpoint 62
preserves its primary reservation outcome through cleanup. The production
reclaimer now performs checkpoint 63 before its final budget check, so a finite
remover resumed after expiry leaves a helpable `RemoveRequested` transition
rather than claiming `Reclaiming` with a stale deadline.

The clean `009-r2-pre-final4-pr` diagnostic passed all 24 rows on exact commit
`ca200238423877044d841a3b92f93edb37385d46`, tree
`5f4454fa25bbfb67ea0e687153ef94470ca11112`, and source-manifest SHA-256
`952FB17E4D08B5C4973A0809F0FC3541E5409B32991A07C6416709CB6C0CD5BF`.
It passed 1,014/1,014 tests, exact churn, all 50 SC-017 configurations, and all
108 suspension rows. Its summary SHA-256 is
`3D543FBF5E4C4A5C514C1C3309565F0B2575126E5B176B79E347802AC46E331A`.
Independent production, qualification-harness, and test/spec re-reviews found
no unresolved High or Medium issue. The accepted Low observations are limited
to generation-fenced, non-visible directory residue after a precisely timed
crash, which recovery/reuse can clean and which cannot project or overwrite
bytes, and the bounded Linux cold-open cost of scanning a flat rendezvous
namespace after extreme historical store-name churn, which does not touch an
open store's hot path.
These results establish convergence only. The decision remains pending until
the new final paths below pass on one immutable source state.

### Rejected R2 final invocation

The R2 Linux command was mistakenly started by Windows PowerShell. The preserved
schema-3 report at
`artifacts/lock-free-os-validation/009-final-r2-linux-x64.json` correctly
classified the host as Windows and returned `not-qualified`: every completed
architecture, atomic, raw, no-lock, crash, Release-test, Docker, sample, and pack
row passed, but Windows lacked the required native/Python prerequisites and
Linux-only tiny-performance, `strace`, and SIGSTOP rows were optional and
inapplicable, so the report could not satisfy the intended Linux contract.
Its SHA-256 is
`090651C119CADD7DAC2C545D04F595FD9E65F5CEFAFC1B9B79EDA009F66EAA7C`.
No production or harness defect was implicated, but the immutable R2 candidate
is rejected in full.

The replacement command now explicitly enters an Ubuntu login shell before
running PowerShell. A Linux-x64 validation-only execution passed every
structural row with report SHA-256
`AE16F3AED1A9E0FE5113F20AD3AB2F28AE194E5CF0717F6372BF87362A51666C`.
That validates host selection and orchestration, not release evidence. The R2
exact-HEAD product, harness, and freeze reviews found no new High or Medium
issue; the accepted cold-open Low remains unchanged.

### Rejected R3 final prerequisite

The corrected Ubuntu command reached Linux x64, but the R3 report stopped at
its first executable row before restore or any store workload. `dotnet --info`
exited 1 because the package-managed SDK 10.0.109 consumed stale user-local
workload-set 10.0.102 metadata with missing manifests. The immutable report is
`artifacts/lock-free-os-validation/009-final-r3-linux-x64.json`, SHA-256
`59419F78D559829A0E3B47AE1FAF334B5B79E83CAF8004646A1FE3687C5B86D5`.
All nine structural self-tests passed; no production row ran. This is an
environment prerequisite failure, not a product or harness finding, but it
rejects R3 in full.

With no workload installed, `dotnet workload repair` was correctly a no-op.
`dotnet workload update` advanced the user-local manifest set to 10.0.109.1.
The repaired executable Linux architecture diagnostic then passed
`dotnet --info`, clean restore/build, and 45/45 tests; its report SHA-256 is
`799A41E8181AA299E0E0E925E3F2818935F2F1842EA94FC66C1AC6E1D6EA7F0A`.
The R4 sequence adds a non-artifact-consuming prerequisite probe before
allocating its immutable Linux path. Product code and the accepted cold-open
Low are unchanged.

### Rejected R4 candidate and disposal test-contract correction

R4 used clean commit `b19d982b76f2f54ebfb9f72e0f2aef85eb2632b8`, tree
`c5a94aeff5f098915f77ef9a0af8ed8e8f730571`, empty-status SHA-256
`E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855`,
and source-manifest SHA-256
`7033447087954594BC982FF2AD859D282527B1A2F342199F404CA9358AEAE921`.
Its Linux report passed all 28 required rows, the 1,014-test suite, native,
Python, Docker, samples, package validation, crash/trace/SIGSTOP gates, and the
exact raw performance matrix. Independent recomputation found H0/M0/L0. The
report, evidence tree, and raw-performance SHA-256 values are respectively
`1346A080DFC7B397437F26088C16C074B9BA1AE3A4E3515D2E07D2DDAA7E2BDE`,
`6DEEBF6450AA909DA6D81E1F9725F9B3CD8DFE0534AE24FE7FC2985E828A5596`,
and `2716C25672FDB332345059F83FAFDD0063E9CE8C6635B7FE198E9A4053FF6D41`.

The same-source R4 PR summary correctly failed after 1,013/1,014 tests. Its sole
failure was the `RecoverLeases` row of
`EveryOperationAndTokenCallbackHasOnlyDocumentedDisposalRaceOutcomes`; the
summary SHA-256 is
`7A3754C670A3622C569CB5E6AFFF479C9C668D20975E31641113337213FAD177`.
The theory raced `RecoverCurrentProcessLeases: true` with a second same-process
handle's ordinary lease work even though the administrative override requires
process-wide quiescence. The reservation row carried the symmetric latent
defect. A legal recovery could therefore invalidate the second handle's lease
between acquire and projection; this was an invalid test schedule, not a store
corruption or progress failure. The same theory also dereferenced borrowed span
bytes even though racing disposal could end their public lifetime.

The correction is confined to the disposal integration test: both concurrent
recovery cases now select `false`, and projection races assert only the safe
empty/full length outcome. Content is still verified through the unaffected
second handle and independent publication/acquisition suites. An unbound local
stress observation completed 50 repeated 15-row theory executions; it is not
promoted as durable evidence. The provenance-bound run passed 29/29 disposal-
class tests, Integration 302/302, the full solution 1,014/1,014, and independent
diff review closed with H0/M0/L0.
The clean official `009-r5-pre-final-pr` diagnostic then passed all 24 gates on
commit `527d451bd124dbbe8880fcb909b6e1bd70ad222a`, tree
`845d5f7f1ec7e816e96de6531810bf01549e8f12`, and source-manifest SHA-256
`6988C966FD0514309635708EA5EB898F0B500D83609507EA6C30B63C55F228D7`.
Its summary and synchronization-probe SHA-256 values are
`A7C28308FCF34CF8D5703CAAAFC9039CEA839EE4AF41CBA4752B9A3455B4C8EF`
and `4ECA1C4A75DFF514FBD3AB54D6533A3DBB77DF5AD024C5A2311287301E9A8DA8`.

R4 remains rejected because its PR tier failed and emitted no sync probe; its
nightly and release tiers were not run. The R4 Linux pass is valuable diagnostic
evidence but cannot qualify the changed R5 source. Production synchronization,
layout, public API, and the accepted Low observations remain unchanged.

### Rejected R5 release convergence and R6 harness closure

R5 froze clean source commit `0a1babaf2b72234a6aac3ef12121a7fee4cf594b`
after the test-only disposal correction. Its immutable Linux, PR, and nightly
artifacts passed, so they remain valuable historical evidence. The release run
is nevertheless rejected and preserved incomplete at
`artifacts/lock-free-qualification/009-final-r5-release`: its first Legacy
mixed-churn trial had not completed after more than 141 minutes because the
harness imposed 100,000,000 operations on the named-semaphore baseline. Fourteen
workers remained live and a read-only mapped sequence advanced by 27,120 in 30
seconds, proving slow progress rather than deadlock. The remaining two Legacy
mixed trials, Legacy 100,000-frame ingest, and all LockFree rows could not fit
the six-hour whole-step deadline. R5 therefore cannot qualify the release.

R6 preserves both profiles and every scenario. It changes completion policy at
the qualification boundary: Legacy mixed-churn and large-ingest use the same
10-second warm-up and 60-second measurement as other duration rows; LockFree
mixed-churn and large-ingest retain exact per-trial durability counts. Probe
schema 8 records `CountBoundProfiles`, `OperationTarget`, `FrameTarget`, and the
duration grace; minimum-compatible schema 8 prevents older global-target
readers from qualifying the report. Both importers reject missing, inherited,
swapped, dual, noncanonical, short-duration, or unmet evidence.

The duration controller is an isolated-process hard boundary, not cancellation
of an in-flight store call. It arms before store creation, tracks every child
start, remains live through warm-up, measurement, result collection, child
cleanup, store disposal, and affinity disposal, and latches completion against
an absolute `Stopwatch` deadline. A late or fired deadline gives child-tree
termination 100 ms of best-effort work and then calls `Environment.FailFast`
unconditionally. Count-bound rows and fixed-work sticky-overflow do not arm this
watchdog. Five direct watchdog cases cover delayed/early dispatch, ordinary
completion, late completion, and a timer forcing `Completing -> TimedOut` while
the completing thread is blocked. Focused runtime smoke, synthetic importer
mutations, and independent adversarial review close
the R6 harness with no unresolved High or Medium finding. Production layout,
synchronization, public API, and steady-state paths are unchanged.

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

1. [artifacts/lock-free-qualification/009-final-r6-pr/summary.json](../../artifacts/lock-free-qualification/009-final-r6-pr/summary.json)
2. [artifacts/lock-free-qualification/009-final-r6-pr/sync-probe.json](../../artifacts/lock-free-qualification/009-final-r6-pr/sync-probe.json)
3. [artifacts/lock-free-qualification/009-final-r6-nightly/summary.json](../../artifacts/lock-free-qualification/009-final-r6-nightly/summary.json)
4. [artifacts/lock-free-qualification/009-final-r6-nightly/sync-probe.json](../../artifacts/lock-free-qualification/009-final-r6-nightly/sync-probe.json)
5. [artifacts/lock-free-qualification/009-final-r6-release/summary.json](../../artifacts/lock-free-qualification/009-final-r6-release/summary.json)
6. [artifacts/lock-free-qualification/009-final-r6-release/sync-probe.json](../../artifacts/lock-free-qualification/009-final-r6-release/sync-probe.json)
7. [artifacts/lock-free-qualification/009-final-r6-release/os-validation.json](../../artifacts/lock-free-qualification/009-final-r6-release/os-validation.json)
8. [artifacts/lock-free-os-validation/009-final-r6-linux-x64.json](../../artifacts/lock-free-os-validation/009-final-r6-linux-x64.json)
9. [artifacts/lock-free-os-validation/009-final-r6-linux-x64.evidence/linux-tiny-performance.json](../../artifacts/lock-free-os-validation/009-final-r6-linux-x64.evidence/linux-tiny-performance.json)

Approval requires the machine-checkable evidence to show that:

- the PR, nightly, and release profiles completed with their required checks
  passed;
- the release synchronization probe and Windows OS validation passed;
- the Linux x64 OS validation passed its required checks, including the exact
  schema-8/minimum-8 one/eight-process tiny-operation raw matrix; one-process intrinsic
  p99, eight-process throughput, <=3x self-amplification, <=10 us p99, and
  every-lock-free-trial 10 ms stall ceilings for both scenarios;
- both exact sibling OS evidence trees, manifests, executable logs, and accepted
  report/tree digests passed initial and completion validation;
- every artifact identifies the same clean commit and identical source manifest;
- the independent qualification-harness and production-code re-reviews have no
  unresolved High or Medium finding; and
- the tracked tree was not edited after evidence collection.

No final release-evidence status, commit hash, source-manifest hash, or artifact
hash is asserted in this review before those files are produced. If the nine
artifacts satisfy the conditions above, the release decision is **final
approved**. Otherwise the code and harness review remains closed, but the
feature is **not approved for release** until the failing gate is rerun on one
identical clean source state.
