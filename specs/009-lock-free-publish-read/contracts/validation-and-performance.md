# Validation and Performance Contract

## Evidence layers

No single stress run proves this protocol. Release evidence combines all five
layers below.

### 1. Deterministic transition schedules

Production atomic transitions expose stable internal checkpoint IDs through a
zero-cost generic/static instrumentation seam. The production no-op type must
inline away; tests use a controlled scheduler. A coverage test fails when a new
transition checkpoint lacks before/after ordering-point, pause, crash, and race
classification. The cross-process agent uses an internal friend-only factory to
instantiate the same generic protocol core with checkpoint instrumentation;
ordinary public construction always selects the inlined no-op specialization.

Required families include:

- same-key insertion/help with unrelated primary-lane mutation;
- primary-to-overflow spill and exact unlink;
- commit/acquire and incomplete commit;
- acquire/remove and 12-reader pending removal;
- release/reclaim and exactly one reuse;
- abort/commit/recovery;
- recovery/live release;
- stale descriptor/binding/reservation/lease after reuse;
- a helper paused after every operation/location validation and resumed only
  after another participant completes the mutation, reclaims the slot, and
  reuses it for a later generation;
- generation and record-incarnation retirement;
- disposal/operation and retained borrowed-memory lifetime.
- participant registration/exhaustion/close/recovery and a crash immediately
  after the first participant-bearing slot/lease claim CAS;
- cancellation/deadline immediately before and after every public ordering point,
  including verification that no owner-controlled claim remains on a canceled
  pre-ordering path;
- intent publication immediately before directory discoverability, including
  rejection of every unknown intent on a current discoverable lifecycle and
  tolerance of stale bytes in `Free`, `Retired`, and pre-metadata
  `Initializing`;
- explicit reservation immediately before and after
  `Initializing -> Reserved(ExplicitReservation)`, and atomic convenience
  publication immediately before and after both its tentative
  `Initializing -> Reserved(AtomicPublication)` transition and its public
  `Reserved -> Published` ordering point;
- same-key contenders proving that `Reserved(ExplicitReservation)`,
  `Published`, and `RemoveRequested` can justify `DuplicateKey`, while
  `Initializing` and `Reserved(AtomicPublication)` require help/revalidation;
- physical capacity exhaustion while the final slot is held in tentative
  `Initializing` or `Reserved(AtomicPublication)`, followed by exact capacity
  restoration after abort/reclaim;
- normal reservation recovery preserving every live Active owner, plus the
  administrative current-process override only under the documented
  process-wide writer/writable-view quiescence precondition;
- Insert cancellation handed to `Unlink/Prepared`, with a delayed location
  publisher classifying target states that are exact old binding, empty, valid
  in-range replacement, malformed, and mapping-out-of-range;
- two `Unlink/Prepared` helpers recovering distinct same-generation cells, with
  the first valid location publication winning and the loser clearing only its
  exact recovered binding, plus `Unlink/TargetSelected` observing each of exact,
  empty, valid replacement, malformed, and out-of-range target state at a
  same-generation alternate location;
- source loss immediately after a location CAS, both before terminal Unlink and
  after a committed Insert successor, proving exact old target/location cleanup
  without successor loss; and
- older location residue, true future-generation reuse, and a future location
  enclosed by an otherwise exact stable old-generation tuple, including a
  forced no-op-confirmation race that must retry rather than report corruption.

Every pair is scheduled immediately before and after each ordering point. Only
the outcome sets in the feature spec are accepted.

### 2. Bounded linearizability checker

Generate small histories with 2-4 actors, 1-2 keys, and 6-12 calls. Record call
intervals, inputs, status, returned generation/bytes, and logical token IDs. An
offline backtracking checker enumerates sequential orders that preserve real-time
precedence against a simple reference model containing absent/reserved/published/
removed values, shared leases, fixed slot/lease capacity, commit/abort, removal,
and stale tokens. The model distinguishes `Reserved(ExplicitReservation)` from
the non-public tentative claim inside an atomic convenience publication:
`TryReserve` orders at explicit `Reserved`, whereas `TryPublish` and
`TryPublishSegments` remain one abstract call that orders at `Published`.
Duplicate-key transitions use only the intent-aware witnesses listed above.

`StoreFull` is validated as a physical-capacity outcome, not invented as key
ownership for a tentative publication. Production history capture therefore
records a semantic proof candidate after the first all-non-Free collect and a
separate confirmation only after the complete second collect matches. A strict
history accepts `StoreFull` only with its own distinct confirmed proof satisfying
`operation entry < candidate < confirmation < operation return`. The candidate,
not the later confirmation/checkpoint callback, is the physical ordering point.
An unconfirmed candidate, a candidate outside the call interval, or a delayed
claim/free timestamp cannot justify capacity. Exact Claim/Free/Retire events
remain lifecycle-integrity coverage only; the checker does not reconstruct a
possibly false simultaneous-full interval from delayed callbacks. A history
without the required confirmed proof cannot use an internal lifecycle to excuse
`StoreFull`.

`LeaseTableFull` uses the same strict evidence rule with a distinct lease-proof
kind and the configured `LeaseRecordCount`: one candidate plus a later
confirmation must satisfy
`operation entry < candidate < confirmation < operation return`. The strict
checker rejects missing, out-of-window, wrong-capacity, or reused lease proofs.
Production-backed histories cover stable lease exhaustion and a release moving
between collects; the latter rejects the candidate and an Infinite caller
retries to success. After confirmation, acquire revalidates the exact directory
binding and Published generation so the proof and target existence share a
valid ordering instant.

Deterministic schedules include stable full tables, a reusable slot moving
between collects, same-handle proof-guard contention, progress through a second
handle, malformed words in either pass, cancellation/NoWait/finite/Infinite
policy outcomes, and a failed pre-metadata claim proving the original control
word never reappears. Lease schedules additionally cover every malformed
state/incarnation/owner/token shape, target removal before the capacity
candidate, record movement, and the per-handle proof guard. Warmed full and
unstable-proof loops are included in the exact zero-allocation gate.

Runs use reproducible seeds and minimize/print a failing history. Independent-key
histories are partitioned only when global capacity cannot couple them. The raw
memory-order path does not log through shared atomic counters that could add
fences and conceal a defect.

### 3. Cross-process checkpoint and crash agent

A C# test agent opens either profile, executes one requested operation, prints a
machine-readable checkpoint, and waits for controller input. The controller may
continue or kill it and then proves healthy same/unrelated-key outcomes,
recovery, fill-to-capacity restoration, and stale-token rejection.

Required platform modes:

- portable Windows/Linux cooperative checkpoint pause;
- `Process.Kill(entireProcessTree: true)` crash;
- Linux `SIGSTOP`/`SIGCONT` smoke;
- Linux `docker pause`/resume/kill where container prerequisites are available.

External Windows thread suspension is optional diagnostic coverage, not a gate,
because a deterministic in-protocol checkpoint is safer and reproducible.

### 4. Raw Release memory-order tests

Independent processes run without scheduling/logging hooks. A producer fills
sequence, complement, key identity, generation, and deterministic full-payload
patterns before commit. Readers verify every byte; removers reuse slots
aggressively. Any partial, torn, stale, or mixed pattern fails immediately.

Layout tests assert every atomic word's exact 8-byte alignment, one-width access,
publication order, and nonreuse while a lookup/lease can reference a generation.
They also assert required-features mask 7, `PublicationIntent` at exact slot
offset 52, PID-namespace identity/mode at header offsets 264/272 and participant
offset 32, their state/ordering rules, and fail-closed rejection between current,
required-features-zero, bit-0-only, and mask-3 draft mappings before payload projection.
The primitive suite includes a cross-process two-word Dekker test mirroring
lease activation versus logical removal: after each side performs its SC
`Interlocked` RMW, both sides observing the other's old value is forbidden.
Windows/Linux x64 are mandatory. A Linux ARM64 weekly/release job is required
before a later compatible protocol advertises ARM64; current v2 open tests require
non-x64 to return `UnsupportedPlatform`.

### 5. Stress, allocation, tracing, and benchmarks

Correctness stress uses unique generation patterns, reproducible PRNG seeds,
status histograms, early/late latency windows, and an actual fill-to-capacity
check after churn/recovery.

Exact allocation gates run on a dedicated warmed thread, settle GC, sample
`GC.GetAllocatedBytesForCurrentThread`, execute at least 1,000,000 complete
cycles, and perform no lambda, assertion, formatting, or result collection in
the measured region. BenchmarkDotNet `MemoryDiagnoser` is supporting evidence,
not the exact-zero oracle.

## Absence of the operation lock

Use three independent checks:

1. Inject a counting/throwing `ISharedStoreSynchronization`; reset after open and
   assert zero calls for every v2 steady-state success/normal-failure path.
2. Hold the legacy Windows named synchronization object or Linux `.lock` byte
   range indefinitely using the existing foreign-lock harness; warmed v2 data
   operations must continue.
3. On Linux, trace a marked warmed measurement interval with
   `strace -ff -yy -e fcntl,flock` and assert no store `.lock` `F_SETLK`/
   `F_SETLKW` call.

Windows ETW wait tracing is useful supplemental evidence but not a deterministic
release gate. Cold create/open/final-cleanup activity is excluded from the
steady-state interval and reported separately.

## Benchmark methodology

The multi-process harness emits JSON and records:

- repository commit, package/layout/resource versions;
- OS build, architecture, .NET runtime/GC mode;
- CPU model, logical/physical count, assigned CPU set/affinity, memory;
- store dimensions and profile;
- exact key/payload/descriptor distribution and collision construction;
- process roles, lease duration, churn pattern;
- 10-second minimum warm-up and 60-second measurement;
- three trials and median reported trial;
- aggregate/per-process throughput, fairness, p50/p95/p99, maximum sampled
  latency, producer allocation counts, whether any copy counter is actually
  instrumented, structural copy-path evidence, and every status count.

Do not oversubscribe the machine. Give each participant one logical processor
where possible. If required participants cannot be assigned, report the workload
as not qualified rather than treating an underprovisioned result as a product
failure.

## Required workload matrix

| Workload | Participants | Pattern | Required evidence |
|---|---|---|---|
| Tiny operation | 1, 2, 4, 8, 12 processes | 8-byte rotating keys, 1-byte values, publish/remove and acquire/release | ops/s, p50/p95/p99, Windows 4x/80% target; Linux one-process intrinsic p99, eight-process throughput, <=3x self-amplification, <=10 us p99, and no >10 ms sampled stall |
| Same-key broadcast | 1, 2, 4, 6, 8, 12 readers | one 256-byte value, full checksum, immediate release | 6-reader >=4x and 12-reader >=7x single-reader throughput |
| Distributed keys | 1, 2, 4, 6, 8, 12 readers | 256 uniform stable keys, 256-byte values | 6-reader >=4.5x and 12-reader >=8x, zero false misses/checksum errors |
| Broker-directed primary | one zero-copy producer, 1 or 12 assigned readers, one observer | 1.3 MB frames, 16-byte descriptors, 256 rotating keys; test pipe sends keys only | 12-reader publication rate >=80% of one-reader, end-to-end latency, `ProducerStoreOperationAllocatedBytes == 0`, and structural direct-reservation-write/borrowed-lease-read evidence; the retained non-instrumented `FullPayloadCopies` field is not treated as a measured zero |
| Mixed churn | 12 readers, 2 publisher/removers | >=256 live collision-heavy keys, 10,000,000 cycles, including keys sharing one canonical bucket | correctness/capacity, zero stale-helper mutation, late missing/publish p99 <=2x early p99 |
| Participant suspension | distributed and churn roles | pause 30 s at every checkpoint | same healthy set >=90% own baseline on suitable keys/capacity |
| Large ingest | one producer, 1 and 12 readers | 100,000 direct 1.3 MB frames | exact bytes, allocation/copy evidence, throughput |

The test broker is a lightweight test-only pipe protocol. It sends committed
keys and expected generations and receives application-level acknowledgements;
none of that state enters the production package or mapping.

## Test tiers

The qualification runner records three independent seeded counts. Production
race repetitions (`SMS_PRODUCTION_RACE_REPETITIONS`) execute the real mapped
`MemoryStore` action pair and are the only count credited to SC-011. Production
generated histories (`SMS_PRODUCTION_HISTORY_COUNT`) capture 6-12 real calls
from 2-4 actors for the reference checker and failure minimizer. Reference-model
checker invocations (`SMS_CHECKER_HISTORY_REPETITIONS`) exercise model/order
coverage only and are never reported as production race executions.

### Pull request gate

- build/analyzers and all existing fast unit/contract tests;
- deterministic schedules for the minimal lifecycle and directory descriptor;
- bounded linearizability smoke with fixed seeds;
- exact layout/alignment/API/profile/compatibility fixtures, including required
  mask 7, publication intent at slot offset 52, and PID-namespace identity/mode;
- checkpoint pause/crash smoke for every transition family;
- raw visibility smoke;
- participant default/sizing/exhaustion/first-claim/reuse contracts;
- zero-allocation one-million-cycle gates;
- hostile held-legacy-lock test;
- Release package creation and package-consumption smoke.

### Nightly Windows and Linux

- randomized/minimized linearizability histories;
- 10,000,000-cycle churn;
- complete checkpoint/pause/crash matrix;
- Linux syscall trace;
- short multi-process performance matrix;
- disposal, collision/overflow, capacity, retirement, and recovery stress;
- Docker pause/recovery where supported.

### Weekly/release qualification

- complete 60-second, three-trial workload matrix;
- 100,000,000 mixed operations;
- 100,000 direct 1.3 MB frames (at least 130 GB written);
- 10,000 injected reservation/lease-owner termination cases;
- all 1,000,000-repetition race-family stress requirements after finite
  deterministic schedules;
- legacy/new package, same-name incompatibility, upgrade/rollback, native/Python
  rejection, full tests, and pack;
- Linux ARM64 memory-order run when that target is available/advertised.

Windows x64 and Linux x64 qualification are distinct mandatory result sets. Each
runs the primitive mapped-atomic litmus and the raw full publish/acquire/remove/
reuse pattern test with production checkpoints/logging disabled. Missing access
to one platform is recorded as **not qualified**, never as pass. The same raw
test fails on any torn, partial, stale, or mixed generation and does not use
shared diagnostic counters in its measured path.

## Qualification evidence contract

The release gate is the schema-v4 output of
`scripts/run-lock-free-qualification.ps1`, not the exit status of any single
test or benchmark command. It explicitly restores the solution, runs the full
Release suite with TRX evidence, and then runs configured focused stresses. A
configured repetition/case count is credited only when its exact family
completion marker or TRX row count is present. The three zero-owner/leak claims
are mapped to named passing tests that inspect final diagnostics and prove full
slot/lease/participant capacity; copying their labels from configuration is not
evidence.

Every performance report must contain exactly one row for each required
profile/scenario/process-count/trial tuple and exactly one summary row per tuple.
Unexpected, missing, duplicate, smoke-only, oversubscribed, affinity-incomplete,
wrong-duration, wrong-frame-target, or wrong-operation-target rows cannot satisfy
the gate. Correctness counters are checked before threshold calculations.

The Linux-x64 `-Command all` report contains one required
`linux-tiny-performance` row. It runs schema-6 SyncProbe mode `sync` for exactly
Legacy and LockFree, `acquire-release` and `publish-remove`, process counts 1 and
8, 10-second warm-up, 60-second measurement, and three trials. All 24 raw rows
must be qualification measurements with zero failures, no oversubscription,
complete unique per-row affinity, internally consistent operation/status/worker
counters, and reproducible 8-row medians. Every raw row has at least two recorded
store operations per completed cycle and exactly one successful operation pair
per cycle (`Acquire`/`Release` or `Publish`/`Remove`); checksum-mismatch and
corruption-reason histogram rows are forbidden. For each scenario, lock-free
one-process median p99 divided by legacy one-process p99 is at most 1.0;
lock-free eight-process median throughput divided by legacy eight-process
throughput is at least 1.0; lock-free eight-process median p99 divided by its
own one-process median p99 is at most 3.0 and is at most 10 microseconds
absolute. Every individual lock-free raw row at either process count—not only
its median maximum—has `MaxMicroseconds <= 10000`. The same OS row remains
visible as optional/not-qualified on Windows and executes no workload there.

Affinity CPU identifiers are unique native identifiers in `[0,63]` that the
probe reports as successfully applied, matching its current 64-bit native
affinity mask. They are not required to be less than the process-visible
logical-processor count because constrained Linux CPU sets can expose sparse
identifiers (for example, CPUs 8 through 15 with a visible count of eight).

The release summary consumes two schema-v3
`scripts/validate-lock-free-os.ps1 -Command all -Configuration Release`
reports: one `windows-x64` and one `linux-x64`. Both must pass every row marked
`required`, and both must match the release runner's repository commit and
normalized source-manifest SHA-256. Platform-inapplicable or explicitly optional
rows remain visible with `required: false`; missing required tools or platform
evidence produces **not qualified**.

For each OS report, the release runner derives the exact sibling `.evidence`
root from the report path. Manifest paths must be unique, normalized repository-
relative paths contained by that root; no report, root, component, or descendant
may be a reparse point. The manifest and recursive actual file set must be
identical, every length/SHA-256 must match, and every executable result's stdout
and stderr path/hash must bind one manifested file. Every passing executed row,
including `clean`, must retain its command and bound logs; only structural
`self-test-*` rows and optional not-qualified platform rows are exempt from that
executable-row requirement. The Linux raw performance JSON is validated again
by the release runner, including provenance and tested-assembly hashes. Accepted
OS report hashes and canonical evidence-tree digests are recorded in the
schema-4 release summary and revalidated before completion.

Both scripts use new evidence paths and refuse to overwrite an existing result.
They record commit/tree/status/source, script/config/solution, runtime/toolchain,
stdout/stderr, and artifact hashes. Exit code 0 is pass, 1 is failure, and 2 is
not-qualified. `-ValidateOnly` performs structure/configuration checks without
workloads and emits `overallStatus: validation-only`; it is never qualification
evidence. See `release-qualification.md` for the current result set and preserved
historical failures.

## Convergence gates

Implementation stops and raises a design flag when a gate fails with the same
underlying invariant after two evidence-driven protocol corrections, or
immediately when correction would require a forbidden global owner, unavailable
128-bit atomic, weakened capacity/correctness contract, native runtime shim, or
new material public semantic choice.

1. **Atomic layout**: no unaligned/mixed-width atomic or partially initialized
   published reference; mapped Interlocked litmus passes Windows/Linux x64.
2. **Minimal lifecycle**: one-key reserve/commit/acquire/remove/release/reuse
   passes every controlled schedule and the reference model.
3. **Directory**: same-key help/spill/unlink creates no duplicate current binding,
   stable-key false miss, early capacity loss, unhelpable descriptor, or
   old-generation helper mutation after slot reuse. Every nonzero operation and
   location matches the exact mutation/slot generation. A spill summary is
   Present before its cell, changes only by exact version CAS, reaches versioned
   Empty only after a stable full scan, and cannot be cleared or resurrected by
   delayed helpers after later generations. Directory-location handoff schedules
   pass joint-tuple/no-op confirmation, first-publisher arbitration, alternate
   location cleanup, and post-CAS source-loss rollback without false corruption
   or removal of a committed successor or valid replacement.
4. **Reclamation**: pause/crash cannot reclaim live ownership, mutate through a
   stale token, leak safely recoverable capacity, or block unrelated progress.
5. **Platform**: raw Release visibility passes and steady-state code never
   touches the OS operation lock.
6. **Performance**: short profiling shows unrelated-key scaling and no material
   uncontended regression after hot cache-line/dispatch causes are corrected.
7. **Release/code review**: long stress, compatibility, package, or independent
   concurrency review has no unresolved invariant violation.

Threshold failure caused only by an unqualified/undersized machine is reported
as missing qualification evidence, not silently passed. A correctness failure is
never averaged away by throughput.
