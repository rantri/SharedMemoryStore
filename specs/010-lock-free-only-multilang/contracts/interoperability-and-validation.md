# Interoperability and Validation Contract

## Purpose and Normative Inputs

Release qualification proves that the managed, native, and Python
distributions implement one SMS2 protocol rather than merely sharing similar
public APIs. The normative protocol inputs are:

- [`protocol/layout-v2.0.md`](../../../protocol/layout-v2.0.md),
- [`protocol/fixtures/v2.0/manifest.json`](../../../protocol/fixtures/v2.0/manifest.json),
- [`protocol/resource-naming-v2.md`](../../../protocol/resource-naming-v2.md), and
- the feature requirements and success criteria in [`../spec.md`](../spec.md).

No release gate may depend on executing the retired implementation or comparing
current results with a live Legacy row. Historical measurements may inform a
threshold change, but accepted evidence contains only explicit absolute SMS2
requirements.

## Runtime Identities

The validation harness uses these stable runtime identifiers:

| Identifier | Distribution | Execution boundary |
|---|---|---|
| `dotnet` | NuGet `SharedMemoryStore` | independent .NET process and assembly |
| `cpp` | installed CMake/native distribution | independent native process using the installed library |
| `python` | installed Python wheel | independent interpreter loading only its packaged native artifact |

Every cross-runtime test uses separately opened mapped views and separately
constructed public wrappers. In-process calls through two bindings to one local
object do not count as interoperability evidence.

## Canonical Conformance Gate

Before an implementation enters cross-runtime testing, it must pass the same
machine-readable vectors for:

- SMS2 identity, byte order, architecture, required features, and rejection of
  incompatible feature masks;
- every record size, alignment, field location, reserved field, and section
  ordering declared by the canonical manifest;
- required-capacity arithmetic at minimum, ordinary, boundary, and overflow
  inputs;
- every control, binding, participant-token, spill-summary,
  directory-location, and directory-operation codec;
- state and public-status numeric assignments;
- hashing, exact-key comparison, bucket/lane selection, overflow scan order,
  and resource-name vectors;
- canonical empty, reserved, published, leased, pending-removal, spill,
  recovered, and corrupt mapped fixtures; and
- fail-closed handling of truncated, retired-layout, unknown-version,
  malformed-offset, misaligned-atomic, impossible-state, and unsupported-
  feature inputs before payload projection.

All three implementations must report 100% vector agreement. Updating an
implementation-specific expected file to disagree with the canonical manifest
is not an acceptable fix.

## Required 3 x 3 Ordered-Pair Matrix

The first runtime is the creator and primary producer; the second is the
independent opener, consumer, and opposing mutator.

| Creator / producer | `dotnet` consumer | `cpp` consumer | `python` consumer |
|---|---:|---:|---:|
| `dotnet` | required | required | required |
| `cpp` | required | required | required |
| `python` | required | required | required |

Every cell is mandatory on Windows x64 and Linux x64. A same-runtime cell still
uses separate processes and validates the installed/package-consumed artifact.
The matrix must not infer the reverse direction: all nine ordered cells run.

### Full lifecycle required in every cell

For at least 1,000 deterministic-seed and recorded-seed arbitrary binary cases
per cell, the harness performs:

1. Create-new by runtime A and open-existing by runtime B under the same public
   name; compare protocol identity and configured capacities.
2. Publish a binary key, descriptor, and payload in A; acquire and checksum the
   exact bytes in B; release in B.
3. Publish a segmented payload in A with empty and non-empty segment shapes;
   prove B sees one complete logical value and no flattening artifact.
4. Reserve in A, write and advance a strict prefix, and prove B observes
   `NotFound`; finish exact advancement, commit, then prove exact visibility.
5. Abort a second A reservation and prove no runtime can acquire it and all
   capacity is reusable.
6. Acquire the same generation from A and B concurrently; remove from the
   opposing runtime; prove new acquires fail while both existing views retain
   identical bytes.
7. Release leases in both possible final-release orders; prove exactly one safe
   physical reclamation and successful republish of the same key.
8. Race A and B to publish/reserve the same key, including an exact hash
   collision with different key bytes; prove one documented winner per exact
   key and no false duplicate across unequal keys.
9. Exercise no-wait, finite-wait, cancellation-before-ordering, and
   cancellation-after-ordering schedules; accept only the canonical result set
   and prove no leaked slot, lease, or participant ownership.
10. Compare shared diagnostic facts from both runtimes while distinguishing
    handle-local counters.
11. Close B while A remains live, then reopen B and prove A's leases, values,
    and participant remain valid.
12. Close the final handle, recreate the public name, and prove the new store ID
    invalidates every token retained from the previous mapping incarnation.

The payload corpus includes zero bytes, embedded nulls, non-UTF-8 bytes,
maximum valid lengths, empty descriptors and payloads where allowed, exact hash
collisions, repeated remove/reuse, and every invalid input boundary.

## Three-Runtime Mixed Scenarios

Pairwise success is insufficient. Required triad scenarios rotate all three
runtimes through publisher, reservation owner, lease owner, remover, helper,
recovery caller, and final closer roles:

- one runtime publishes while the other two hold simultaneous leases;
- twelve total readers are distributed across all three runtimes and retain one
  checksum through logical removal and arbitrary final-release order;
- all three race to publish one exact key and distinct colliding keys;
- one runtime pauses during directory mutation, a second helps, and the third
  observes or removes the ordered generation;
- one runtime crashes with a reservation or lease, a second performs explicit
  recovery, and the third proves exact capacity reuse;
- participant-table exhaustion and subsequent participant reuse include at
  least one handle from every runtime; and
- concurrent final close/reopen uses different runtimes on each side of the
  cold lifecycle boundary.

The release mixed-runtime workload completes at least 1,000,000 credited public
operations with zero corruption, stale-generation mutation, byte mismatch,
false successful removal, access violation, or safely recoverable leaked
capacity.

## Deterministic Transition Schedules

Each implementation exposes a test-only checkpoint adapter at protocol
ordering boundaries. Checkpoints are absent or inert in packaged production
data paths. The scheduler can pause, resume, cancel, or terminate the checkpoint
process and records the exact seed and transition identity.

For each persistent transition below, every runtime must be the paused/crashed
actor and each other runtime must independently act as helper, observer, or
recovery caller.

### Participant schedules

- physical creation before/after header initialization authority is known;
- participant claim before identity publication;
- identity publication before `Registering -> Active`;
- `Active -> Closing` after local call drain;
- stale-owner `Active -> Recovering`;
- zero-reference scan before/after `Reclaiming`; and
- next-incarnation Free publication or terminal retirement.

### Directory schedules

- metadata-ready operation publication before canonical bucket mutation;
- Insert and Unlink at `Prepared`, `TargetSelected`, `BindingChanged`,
  `Rejected`, and `Complete`;
- before and after primary-lane or overflow-cell CAS;
- competing-location arbitration and exact alternate-location cleanup;
- spill-summary `Present` publication, witness repoint, stable-empty scan, and
  versioned Empty publication;
- source-word reread around slot classification;
- cleanup of strictly older residue and observation of future-generation
  state; and
- exact hash collision, spill churn, cancellation handoff, and slot reuse while
  an old helper is paused.

### Slot and publication schedules

- before/after `Free -> Initializing`;
- ordinary metadata writes before the metadata-ready marker;
- explicit-reservation `Initializing -> Reserved` ordering;
- atomic-publication tentative Reserved state before `Published`;
- descriptor/payload completion before `Reserved -> Published`;
- duplicate arbitration before and after public ordering;
- `Published -> RemoveRequested` with zero, one, and many leases;
- `Initializing|Reserved -> Aborting`;
- stable no-lease scan, directory unlink, helper cleanup, Reclaiming, and next
  Free/Retired publication; and
- cancellation immediately before and after every public ordering point.

### Lease schedules

- record claim before slot binding publication;
- binding publication before `Claiming -> Active`;
- acquire revalidation before borrowed-view return;
- `Active -> Releasing` and exact final slot protection release;
- `Active|Claiming -> Recovering` where permitted;
- record next-incarnation Free publication or retirement; and
- stale-token release after record and slot reuse.

Every schedule asserts both safety and liveness:

- the old actor cannot mutate a later generation after resume;
- unrelated keys continue while capacity permits;
- same-key state remains helpable or returns a bounded documented outcome;
- no partial descriptor/payload becomes visible;
- no live participant is recovered; and
- all state proven abandoned becomes reusable.

The release schedule corpus covers every catalogued transition and at least
1,000,000 total repetitions. Missing checkpoint coverage is a test failure.

## Crash and Recovery Qualification

The crash agent must terminate without running wrapper destructors or orderly
participant close. Fault points cover:

- mapping creation and zero-header initialization;
- participant registration and final close;
- reservation claim, metadata-ready publication, partial advancement, commit,
  and abort;
- atomic publication before and after visibility ordering;
- lease claim/activation/release;
- logical removal, directory unlink, spill-summary maintenance, and slot
  reclamation;
- participant/slot/lease recovery classification and exact recovery CAS; and
- Linux owner-line, anchor, release-marker, sidecar-rewrite, and resource-delete
  boundaries.

At least 10,000 reservation-owner and lease-owner terminations are distributed
across all runtimes and supported hosts. Qualification proves:

- zero partial publication;
- zero recovery of live or ambiguous ownership;
- zero accepted stale-token action;
- zero mutation of a later generation by a resumed or replayed helper;
- complete reuse of all capacity classified as safely abandoned; and
- conservative retention when process, PID namespace, file type, permission,
  or owner evidence is uncertain.

Current-process recovery tests explicitly quiesce every relevant wrapper before
enabling the override. A test that recovers concurrent current-process use is
invalid evidence.

## Corruption and Non-Poisoning Schedules

Raw mapped-state injection covers:

- magic, version, required features, header length, total bytes, section bounds,
  stride, count, and atomic alignment;
- invalid/reserved control bits, zero or wrapped generations, out-of-range
  participant tokens, and impossible owner/state combinations;
- malformed primary/overflow bindings and spill summaries;
- contradictory stable bucket mutation, directory operation, directory
  location, immutable binding, target cell, and slot-control tuples;
- discoverable slot metadata with missing marker, unknown publication intent,
  invalid lengths, or out-of-bounds storage;
- malformed lease binding/incarnation state; and
- terminal Corrupt propagation to already-open and newly opened handles in all
  runtimes.

A corruption test passes only when repeated acquire observations and required
exact no-op CAS confirmation prove the defect stable before one full-word
`Ready -> Corrupt` CAS. A deliberately changing tuple must produce retry or a
bounded contention result and must not latch corruption.

Non-poisoning tests inject invalid caller input, capacity exhaustion, participant
and lease-table exhaustion, cancellation, retry-budget exhaustion, stale but
well-formed references, legal helper races, and ambiguous owner liveness. None
may change Ready to Corrupt.

## Memory-Ordering Qualification

Each runtime executes independent mapped views in separate processes. Python
executes atomic primitives through its packaged native component rather than
claiming Python-level atomics.

Required release-build litmus tests include:

- ordinary metadata/payload writes before release publication and acquire
  visibility afterward;
- full-word compare/exchange atomicity across runtimes;
- sequentially consistent RMW ordering required by the canonical contract;
- acquire revalidation around directory, slot, and lease observations;
- logical removal before new-acquire rejection;
- final lease release before slot reuse; and
- participant identity and PID-namespace mode publication before recoverable
  ownership is referenced.

Each litmus records architecture, OS, compiler/runtime version, iteration count,
and forbidden-outcome count. Any forbidden outcome fails qualification.

## Steady-State Lock-Tracing Contract

Publish, segmented publish, reserve, advance, commit, abort, acquire, borrowed
projection, release, remove, reclaim/help, explicit recovery, and diagnostics
must execute without a process-owned or globally exclusive store-wide operation
lock.

Cold create/open/close coordination is traced separately and may use only the
resource-protocol-2 gates and owner evidence in their documented order.

### Windows x64

The qualification harness records named synchronization creation/open and wait
activity for every child process. The steady-state trace window starts after all
participants are Active and ends before local close. It must contain zero waits
or acquisitions of the store's named cold mutex by data operations. Mapped
atomic instructions and process-local lifecycle entry are allowed.

### Linux x64

Each process is traced with child following and lock syscalls enabled, including
`fcntl`/OFD-lock and `flock` activity. The steady-state window must contain zero
acquisition of the store `.lock` or `.lifecycle` rendezvous and zero owner-anchor
lock operation caused by a data call. Expected cold-open and final-cleanup lock
events must match the documented `.lifecycle -> .lock -> mapping/owner` order
and reverse release order.

Trace acceptance uses exact derived resource paths/names and retains the raw
per-process event tree. Summary-only evidence is insufficient. Any missing
child trace, ambiguous resource identity, or hot-window lock event fails the
gate.

## Absolute Performance and Boundedness Gates

All performance gates run Release builds on an otherwise idle qualified host,
use fixed processor/memory metadata, record warm-up separately, retain raw
samples, and apply controller-enforced monotonic deadlines. No Legacy process or
Legacy result row participates.

| Gate | Absolute requirement |
|---|---|
| Hot-path allocation | Warmed C# and native contiguous publish/acquire/release/remove loops report `0 B/op` or zero allocator calls; segmented/direct-ingest paths allocate no payload-sized temporary buffer. Python may allocate wrapper objects but may not copy a payload-sized buffer on zero-copy acquire/reservation paths. |
| Finite waits | Every credited finite-wait operation completes within its selected timeout plus 250 ms. Any individual over-envelope operation fails the run and owns no leaked token. |
| Linux tiny-operation p99 | Eight-process publish/remove and acquire/release p99 is at most 10 microseconds. |
| Windows tiny-operation p99 | Eight-process publish/remove and acquire/release p99 is at most 25 microseconds. |
| Tiny-operation throughput | Each eight-process scenario sustains at least 100,000 credited public operations/second aggregate on each qualified host. |
| Scaling | Eight-process p99 is at most 3 times the corresponding one-process p99. |
| Raw stall | No successful raw lock-free trial operation stalls longer than 10 milliseconds. |
| Reader fan-out | Twelve mixed-runtime readers acquire one 1.3 MB generation, agree on checksum, survive pending removal, and complete final reclamation without a hot lock or timeout. |
| Long mixed stress | 1,000,000 credited mixed-runtime lifecycle operations complete with zero safety failure and no safely recoverable capacity leak. |

Thresholds are versioned qualification policy. Changing one requires an
explicit specification/contract review and new evidence; silently substituting
a slower host-relative or retired-implementation comparison is forbidden.

## Packaging and Clean-Consumer Gate

Qualification builds and consumes each distribution outside its source tree:

### Managed

- Release build, complete managed test projects, NuGet pack, symbol pack, and
  package metadata/XML documentation checks.
- A clean project restores only the produced package and completes the full
  create/publish/acquire/remove/reservation lifecycle.
- Static API inspection finds no public profile selector or retired creation
  path.

### Native

- Clean CMake configure/build/test/install on Windows and Linux.
- An external consumer uses only installed headers, exported targets, and
  installed runtime artifacts.
- ABI conformance checks fixed-width structures, opaque handles, nonthrowing
  statuses, version identity, and symbol/export completeness.
- Static and dynamic inspection finds no retired-layout engine or fallback.

### Python

- Build source distribution and platform wheel, install each into a clean
  environment, and run the public lifecycle suite and sample.
- The package loads only its adjacent packaged native artifact, not the current
  directory, `PATH`, or an arbitrary system library.
- Runtime dependency inspection finds no undeclared third-party requirement.
- Context-manager and borrowed-view lifetime tests reject use after release,
  completion, recovery, or store close.

All samples, package readmes, compatibility manifests, and migration guidance
must describe SMS2 as the sole current protocol. Historical specifications may
remain historical but must not be packaged as current compatibility guidance.

## Full Suite and Test Tiers

### Pull-request tier

- all static/API checks and canonical conformance vectors;
- all managed unit and contract tests;
- native unit/conformance tests and Python wrapper tests;
- a reduced nine-cell ordered matrix with at least 100 cases per cell;
- one deterministic schedule for every transition family;
- retired/malformed mapping rejection;
- clean package-consumer smoke tests; and
- documentation/link/manifest consistency.

### Nightly tier

- full unit, contract, integration, linearizability, package, sample, and Docker
  suites on Windows x64 and Linux x64;
- all nine ordered cells at 1,000 cases each;
- three-runtime mixed stress and controlled transition schedules;
- crash/recovery, corruption/non-poisoning, memory-order, allocation, and raw
  lock-trace matrices; and
- absolute performance gates with retained raw samples.

### Weekly/release tier

- all SC-001 through SC-013 counts and durations without sampling reduction;
- at least 10,000 cross-runtime owner terminations;
- at least 1,000,000 deterministic pause/reuse repetitions with complete
  transition-catalog coverage;
- the full 1,000,000-operation mixed-runtime workload;
- clean install/consumer tests for every produced artifact;
- Windows and Linux lock tracing and absolute performance matrices; and
- final migration, compatibility, package, sample, and current-protocol
  inspection.

A required environment dependency such as Docker, compiler, supported kernel
locking, or tracing capability is a release-host prerequisite. Qualification
must fail or be rerun on a qualified host; it must not silently convert a
required skipped test into success.

## Evidence Contract

Each accepted run emits a machine-readable report and immutable raw artifacts
containing:

- repository revision and dirty state;
- protocol/manifest digest;
- package, native ABI, compiler, runtime, Python, OS, architecture, kernel, and
  filesystem identities;
- exact tier, runtime roles, seeds, counts, timeouts, and scenario catalog;
- per-cell and mixed-runtime status/byte/checksum totals;
- checkpoint and crash coverage with forbidden outcomes;
- corruption-latch and non-poisoning results;
- raw lock-trace, allocation, latency, throughput, stall, and watchdog data;
- clean-consumer artifact names and cryptographic digests;
- explicit skip list, which must be empty for release-required scenarios; and
- start/end monotonic timestamps and controller exit status.

The controller enforces deadlines, terminates the complete child tree on
timeout, and rejects late completion even when timer dispatch is delayed. The
final release summary revalidates report and raw-artifact digests rather than
trusting copied summary values.

Missing, truncated, stale-revision, digest-mismatched, deadline-exceeded, or
schema-incompatible evidence is a failed gate.

## Release Acceptance

The feature is qualified only when:

1. all three distributions pass 100% canonical conformance;
2. all nine ordered cells and required three-runtime scenarios pass on Windows
   x64 and Linux x64;
3. deterministic, crash/recovery, corruption, memory-order, and lock-trace gates
   pass with no forbidden outcome;
4. all absolute boundedness and performance thresholds pass without Legacy
   comparisons;
5. every clean-consumer/package/sample/documentation gate passes;
6. static inspection finds one current protocol and no public or executable
   retired-layout selection path; and
7. the complete release evidence set is present, internally consistent, and
   digest-verified.
