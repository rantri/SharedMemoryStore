# Implementation Plan: Lock-Free Shared-Memory Key-Value Store

**Branch**: `codex/lock-free-csharp` | **Date**: 2026-07-12 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/009-lock-free-publish-read/spec.md`

## Summary

Add an explicit C# lock-free profile to the existing bounded shared-memory
key-value store while retaining layout-v1.2 as the default legacy profile. The
new mapped layout 2.0 uses only naturally aligned 64-bit atomic control words,
a configurable participant registry (64 handles by default), a fast fixed-bucket
key directory with a capacity-preserving bounded overflow directory,
generation-fenced value slots, generation-tagged directory operation/location
words, per-slot publication intent that separates explicit reservation ordering
from atomic convenience publication, incarnation-fenced lease records, and
cooperative per-record recovery. Persistent mapped structural corruption is
published once through the header's terminal `Ready -> Corrupt` atomic latch;
all later mapped-data operations fail closed without an OS lock.
The lock-free profile caps `SlotCount` at 1,048,575 so every directory helper
state fits in one portable 64-bit atomic word. A public `MemoryStore` facade preserves the
recognizable key-value, reservation, zero-copy lease, removal, recovery, wait,
diagnostics, and disposal workflows. Named cross-process synchronization is
retained only for cold mapping initialization and compatibility validation; no
v2 steady-state data operation acquires it.

## Technical Context

**Language/Version**: C# 14 on .NET 10; mapped protocol documented with
fixed-width, language-neutral little-endian fields.

**Primary Dependencies**: .NET base class library and existing Windows/Linux OS
adapters only; the Linux adapter uses libc `open`, `flock`, and `statx` for
cold-lifecycle owner anchors. xUnit and BenchmarkDotNet remain test/benchmark
dependencies. No new runtime package or broker dependency.

**Storage**: Fixed-capacity named shared memory. Existing mapped layout 1.2
remains unchanged. New mapped layout 2.0 (`SMS2`) contains an explicit header,
participant records, fixed CAS directory buckets, bounded overflow cells,
value-slot metadata and key/descriptor/payload storage, and lease records. The same public store name
resolves to the same physical mapping so a profile mismatch fails closed rather
than opening a parallel empty store.

**Testing**: Existing xUnit unit, contract, integration, interop, package, and
Docker suites; deterministic atomic-transition schedules; a bounded
linearizability checker; cross-process checkpoint/pause/crash agents; raw
Release memory-order litmus tests; zero-allocation loops; BenchmarkDotNet; and a
multi-process benchmark/OS-lock tracing harness. Linux lifecycle tests kill or
pause owners around anchor creation, sidecar commit, release-marker publication,
and cleanup; special-file, symlink, malformed, locked, and orphan artifacts are
also exercised.

**Target Platform**: Windows x64 and Linux x64, including same-host Linux
containers. Layout 2.0 initially rejects non-x64 processes even when they are
64-bit/little-endian. Linux ARM64 is a weekly/release memory-order qualification
target before a later compatible feature/version advertises that architecture.
The Linux anchor adapter treats an unavailable/blocked `statx` call as
ambiguous-live evidence, so metadata uncertainty cannot authorize deletion.

**Project Type**: Reusable NuGet library in the existing multi-language
monorepo. Layout 2.0 is implemented by C# first; current C++ and Python clients
remain layout-v1.2-only and reject v2.

**Performance Goals**: Meet SC-001 through SC-018, including 0 B/op warmed data
paths; on Windows at least 4x legacy aggregate throughput and 80% lower p99 for
the eight-process tiny-operation workload; on Linux no aggregate/p99 regression
and no raw lock-free trial stall above 10 ms in the exact three-by-60-second
eight-process acquire/release and publish/remove matrix; scale the broker-directed 1.3 MB workload from
6 to 12 readers without a store-wide lock; and retain early/late churn p99
within 2x.

**Constraints**: Preserve existing public status numeric values and all legacy
method signatures; no in-place v1.2 conversion; no 128-bit atomic requirement;
no named/global exclusive owner on v2 data paths; no hidden maintenance thread;
no mandatory payload copy; bounded retries/cancellation; fixed value, lease, and
participant capacity; lock-free `SlotCount` in `1..1,048,575`; every persistent
directory helper reference fenced by the exact 33-bit slot generation; trusted
same-host writers; explicit recovery only; and layout-v2 required feature mask
`7` (versioned spill summary, publication intent at slot offset 52, and exact
Linux PID-namespace identity at header offset 264/participant offset 32).
Named/file locking remains permitted only for bounded cold create/open/close
coordination. Each managed Linux handle additionally holds a private regular-file
`flock` anchor after mapping and before its owner-sidecar line is committed. The
anchor is liveness evidence, never a data-operation lock; cleanup removes only a
canonical unreferenced anchor proven regular and unlocked through a separately
opened `O_NOFOLLOW` descriptor. Locked, nonregular, malformed, inaccessible, or
otherwise ambiguous artifacts are retained.
Every v2 operation also acquire-loads the aligned store control before a new
mapped projection or mutation. This is one ordinary shared-memory atomic read,
not a process-held critical section. Only revalidated persistent mapped
structural corruption can full-word-CAS `Ready` to terminal `Corrupt`; caller
input, capacity, contention, cancellation, and legal lifecycle races cannot
poison the mapping.

**Scale/Scope**: One producer and 6-12 broker-directed workers are the primary
benchmark, but the contract supports multiple independent publishers, readers,
removers, observers, diagnostics callers, and store handles. V2 defaults to 64
simultaneously open handles and permits explicit participant sizing. Validation includes
up to 100 million lifecycle operations, 100,000 1.3 MB direct ingests, and 10,000
recovery cases in release/nightly tiers. The v2 value-slot count is configurable
from 1 through 1,048,575; larger capacities require a later layout version.

## Constitution Check

*GATE: Passed before Phase 0 research and re-checked after Phase 1 design.*

- Library and package first: PASS. `MemoryStore` remains a general-purpose
  key-value library facade; broker delivery and worker selection are test/sample
  concerns only.
- Stable contracts and semantic versioning: PASS. Existing signatures, enum
  assignments, resource discovery, and layout 1.2 remain supported. Layout 2.0,
  resource protocol 2, package major-version impact, and rollback are explicit
  and independently tested.
- Test-driven production quality: PASS. Tests precede each engine phase and
  cover atomic layout, public contracts, deterministic races, linearizability,
  crash recovery, disposal, allocation, performance, package consumption, and
  platform behavior.
- .NET 10 baseline and portable core: PASS. C#/.NET 10 is first, while every
  mapped field, state transition, ordering point, owner identity, and memory
  order is documented without managed-object identity. Platform liveness and
  mappings stay behind adapters. The non-portable Linux `flock`/`statx` owner
  anchor is isolated in `Interop`, is absent from mapped layout and hot data
  paths, preserves PID/start fallback for native/Python/older owners, and fails
  conservative on unsupported or ambiguous artifacts.
- Minimal, observable, dependency-conscious design: PASS. No runtime dependency,
  global mutable configuration, console output, broker, or hidden worker is
  added. Maintenance is bounded and cooperatively helped by callers; diagnostics
  are snapshot-based and caller controlled.

The post-design re-check remains PASS. The directory overflow reserve preserves
the configured value-slot capacity even for exact hash collisions, so no hidden
index capacity or index-full status is introduced. Participant records are an
explicit open-handle capacity with a distinct appended open status. They are
claimed only on the cold path; a first slot/lease claim performs one acquire
validation of its own cache-line-isolated record but no hot-path registry RMW or
cross-participant cache-line contention. The approved v2 capacity ceiling makes
the primary/overflow target fit in 22 bits, leaving 33 bits in both directory
operation and location words for the exact slot generation. This closes the
stale-helper ABA window without a non-portable 128-bit atomic or a blocking
quiescence scheme. The same slot ceiling permits a one-word versioned
spill-summary codec (20-bit index, 33-bit generation, Present, reserved bits)
whose required-feature bit fences the earlier pre-release Boolean hint. Exact
Present/Empty CAS transitions restore fast missing lookups after churn without
allowing a delayed setter or clearer to manufacture a false negative.
The second required-feature bit assigns slot offset 52 to immutable publication
intent, so helpers and duplicate classification cannot confuse an explicit
reservation with the internal reserved stage of atomic convenience publication.
The third required-feature bit assigns exact Linux PID-namespace identities to
the store header and participant records, so a PID is never classified through
a different namespace view. Older drafts and current mask-7 clients reject one
another before payload projection.

## Architecture and Dependency Direction

```text
Public MemoryStore / ValueReservation / ValueLease
                    |
                    +--> legacy layout-1.2 engine --> existing synchronized protocol
                    |
                    `--> lock-free layout-2.0 engine
                              |--> terminal mapped-corruption control
                              |--> atomic directory + slot + lease protocols
                              |--> participant registry/recovery classification
                              `--> memory-map and cold-lifecycle platform adapters
                                     |--> Windows mapping/named lifecycle gate
                                     `--> Linux mapping/sidecar + owner anchor

Tests/benchmarks --> public facade + internal deterministic checkpoint seam
Protocol docs ----> every language implementation (C# v2 now; C++/Python reject)
```

`MemoryStore` owns the local mapped-memory lifetime and dispatches to exactly
one concrete engine. Public reservation and lease structs continue to call back
through that facade, but carry profile-neutral lifecycle and record incarnation
tokens. The lock-free engine depends on fixed-width protocol primitives; those
primitives do not depend on diagnostics, test orchestration, or public wrappers.
Platform-specific process-start/liveness classification remains isolated.
On Linux, a handle acquires its per-owner anchor while the per-store lifecycle
gate is held, commits the exact `PID:start:token` sidecar line, and keeps the
anchor locked until its mapped view is gone and the line is absent or covered by
a durable release marker. A later lifecycle action reconciles markers and, after
an atomic sidecar replacement, sweeps only canonical unreferenced anchors that a
separate regular-file descriptor proves unlocked. This closes the crash window
between anchor creation and sidecar commit without introducing a store-wide hot
owner or interpreting foreign PID namespaces as local liveness.

## Project Structure

### Documentation (this feature)

```text
specs/009-lock-free-publish-read/
|-- spec.md
|-- plan.md
|-- research.md
|-- data-model.md
|-- quickstart.md
|-- contracts/
|   |-- public-api.md
|   |-- layout-v2.md
|   |-- concurrency-and-memory-ordering.md
|   |-- recovery.md
|   |-- compatibility-and-rollout.md
|   `-- validation-and-performance.md
|-- checklists/
|   `-- requirements.md
`-- tasks.md
```

### Source Code (repository root)

```text
protocol/
|-- README.md
|-- layout-v1.2.md
|-- layout-v2.0.md
|-- resource-naming-v1.md
|-- resource-naming-v2.md
`-- compatibility.json
src/SharedMemoryStore/
|-- MemoryStore.cs                       # Stable public facade
|-- SharedMemoryStoreOptions.cs          # StoreProfile and v2 helpers
|-- ValueLease.cs
|-- Ingest/ValueReservation.cs
|-- Engines/
|   |-- IStoreEngine.cs
|   `-- LegacyV12/
|       `-- LegacyV12StoreEngine.cs      # Extracted, behavior-equivalent v1.2 path
|-- LockFree/
|   |-- LockFreeStoreEngine.cs
|   |-- AtomicControlWord.cs
|   |-- IndexBinding.cs
|   |-- LockFreeKeyDirectory.cs
|   |-- LockFreeSlotTable.cs
|   |-- LockFreeLeaseRegistry.cs
|   |-- LockFreeParticipantRegistry.cs
|   |-- LockFreeRecovery.cs
|   |-- ParticipantIncarnation.cs
|   `-- LockFreeDiagnostics.cs
|-- LayoutV2/
|   |-- LayoutV2Constants.cs
|   |-- StoreLayoutV2.cs
|   `-- SharedRecordsV2.cs
|-- Lifecycle/
|   `-- StoreLifecycleGate.cs            # Nonblocking operation entry
`-- Interop/                             # Existing mapping/liveness adapters
tests/
|-- SharedMemoryStore.UnitTests/         # State machine, layout, retry, rollover
|-- SharedMemoryStore.ContractTests/     # API, status, package/profile contracts
|-- SharedMemoryStore.IntegrationTests/  # In-process/cross-process races and crash
|-- SharedMemoryStore.InteropTests/      # v1.2 native rejection of v2
|-- SharedMemoryStore.LockFreeAgent/     # Deterministic checkpoint participant
`-- SharedMemoryStore.LinearizabilityTests/
benchmarks/SharedMemoryStore.Benchmarks/ # Single-process allocation/latency
benchmarks/SharedMemoryStore.SyncProbe/  # Multi-process JSON benchmark/tracing tool
samples/LockFreeBrokerKeys/              # Test broker sends keys; store remains KV
```

**Structure Decision**: Preserve public source locations and isolate volatile
layout-2.0 algorithms under `LockFree`/`LayoutV2`. Extract the current v1.2 body
behind the facade without changing its observable behavior. Keep protocol
documents at repository root because all language distributions must understand
which layouts they may open. Test-only scheduling and broker orchestration live
outside the package.

## Delivery Strategy

1. Freeze legacy behavior with characterization and public API snapshots.
2. Add profile/participant sizing, layout-v2 records and participant registry,
   atomic codecs, and cross-process aligned-atomic litmus tests before exposing
   data operations. Slot/lease claim controls atomically embed a participant
   token; no post-claim identity window is permitted.
3. Implement reserve/commit/acquire/remove/release/reuse for one key with
   deterministic checkpoints and a reference-model checker. Every helper phase,
   directory-location publication, and cleanup uses a generation-tagged exact
   CAS so an older helper can only install/clear its own stale reference and can
   never match a reused lifecycle. Generation-mismatch cleanup is directional:
   an older helper preserves every future-generation word, while a current
   helper may exact-clear strictly older residue after fresh ownership
   validation. Publications from all-zero are postvalidated and rolled back
   only by comparison with the exact value just published. Insert helpers also
   reclassify the exact slot after validation windows: a concurrent transition
   to `Aborting`/`Reclaiming` routes to cancellation cleanup, while a changed
   operation or generation ends the stale helper without a corruption result.
   The exclusive claimant writes immutable `PublicationIntent` before
   release-publishing the current-generation `Insert/Prepared` metadata-ready
   marker, which precedes canonical mutation and directory-cell discoverability.
   `Reserved(ExplicitReservation)` is an ordered key
   owner; `Reserved(AtomicPublication)` remains tentative until commit.
4. Add the primary directory and spill-safe overflow, then multi-key concurrency,
   simple/segmented publish, direct ingest, diagnostics, and local disposal.
5. Add exact owner-incarnation recovery and cross-process crash/pause coverage.
   On Linux, publish PID-namespace identities before participation, isolate
   `flock`/`statx` anchors in the cold-lifecycle adapter, unmap before releasing
   liveness, reconcile durable close markers, and repair only canonical unlocked
   pre-sidecar orphans while retaining every ambiguous artifact.
6. Thread the shared store-control capability through every structural validator
   and projection predicate. Verify cross-handle `Ready -> Corrupt` propagation,
   terminal reopen rejection, nonthrowing cleanup, and non-poisoning caller
   failures with corruption injection tests.
7. Complete compatibility rejection, docs/sample/package changes, allocation
   gates, performance matrix, and release qualification. Linux `-Command all`
   owns an explicit raw tiny-operation matrix; the release runner independently
   validates it and the exact non-reparse OS evidence trees, then revalidates
   accepted report/tree digests at completion.

Each stage must keep the minimal lifecycle linearizable. Repeated failure of the
atomic, directory, reclamation, platform, or performance convergence gates in
`contracts/validation-and-performance.md` stops implementation for design review.

## Semantic Version and Deployment

- Mapped layout identity: new major `2.0` with magic `SMS2`.
- Resource protocol: version 2; physical region/lifecycle discovery names remain
  the same so incompatible opens fail closed.
- NuGet: target `2.0.0` because public token representation and concurrency/wait
  semantics expand even though legacy source workflows and enum values remain.
- Default profile: `StoreProfile.Legacy`, preserving compiled/default v1.2 use.
- Adoption: callers explicitly create/open v2 with `CreateLockFree` or `Profile`.
- Default v2 participant capacity: 64 open handles; exhaustion returns the
  appended `StoreOpenStatus.ParticipantTableFull` without disturbing live handles.
- No conversion: drain/close and recreate/republish under the same name, or use a
  new public store name for side-by-side rollout. Rollback likewise recreates a
  v1.2 mapping and republishes application-owned data.

## Complexity Tracking

No constitution violations are planned. The second internal engine, mapped
layout, fault agent, and linearizability test project are required by the
explicit compatibility and lock-free correctness goals; none is a runtime
service or application-specific integration.

## Phase 0 Research Summary

See [research.md](research.md). The capacity-preserving directory design resolves
the material key-index fork without weakening configured slot capacity or
requiring a global rebuild. The approved generation-tagged descriptor redesign
resolves the observed helper/reuse ABA failure by trading an explicit v2
`SlotCount` maximum for a portable single-word proof. Platform atomic behavior
remains an implementation gate proven by executable Windows/Linux tests.

## Phase 1 Design Summary

See [data-model.md](data-model.md), the contracts under [contracts](contracts/),
and [quickstart.md](quickstart.md). All public ordering points, atomic widths,
state transitions, ownership lifetimes, compatibility identities, validation
tiers, and non-convergence conditions are specified before task generation.
