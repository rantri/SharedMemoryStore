# Architecture

This maintainer guide explains the current implementation at a level useful for
review and onboarding. It distinguishes current implementation details from
stable public contracts. Public behavior is governed by the linked contracts,
not by incidental private type names.

Primary contracts:

- [Public API contract](../specs/001-frame-memory-store/contracts/public-api.md)
- [Error taxonomy contract](../specs/001-frame-memory-store/contracts/error-taxonomy.md)
- [Shared-memory layout contract](../specs/001-frame-memory-store/contracts/shared-memory-layout.md)
- [Ingest layout contract](../specs/003-zero-copy-ingest/contracts/ingest-layout.md)
- [Owner recovery contract](../specs/004-store-reliability-hardening/contracts/owner-recovery-contract.md)
- [Disposal and rollover contract](../specs/004-store-reliability-hardening/contracts/disposal-rollover-contract.md)
- [Index health contract](../specs/004-store-reliability-hardening/contracts/index-health-contract.md)

## Responsibility Boundary

The package is a reusable `net10.0` library. Its public responsibility is to
provide a bounded named shared-memory store for opaque byte keys, optional
descriptor bytes, immutable payload bytes, leases, direct reservations,
segmented publish, explicit recovery, and diagnostics.

The package does not own application schemas, frame parsing, persistence,
network distribution, hosting, logging, health checks, dependency injection, or
background cleanup. Those belong to consumers or optional adapters.

## Source Areas

| Area | Current responsibility | Stability |
|------|------------------------|-----------|
| [`src/SharedMemoryStore/MemoryStore.cs`](../src/SharedMemoryStore/MemoryStore.cs) | Public store facade, operation validation, synchronization entry points, lifecycle and diagnostics composition | Public API names are stable contracts; private flow can change |
| [`src/SharedMemoryStore/Layout/`](../src/SharedMemoryStore/Layout/) | Shared header, slot, key-index, lease record, layout constants, lifecycle identity | Shared record layout and state values are compatibility contracts |
| [`src/SharedMemoryStore/Slots/`](../src/SharedMemoryStore/Slots/) | Slot reservation, writing, reading, reclaiming, remove/reuse transitions | Internal algorithms can change when contracts and tests remain valid |
| [`src/SharedMemoryStore/Ingest/`](../src/SharedMemoryStore/Ingest/) | Reservation token backing, reservation recovery, segmented publish helper | Public reservation semantics are contracts; helper internals can change |
| [`src/SharedMemoryStore/Leasing/`](../src/SharedMemoryStore/Leasing/) | Lease registry, release, owner classification, recovery | Public lease and recovery outcomes are contracts |
| [`src/SharedMemoryStore/Diagnostics/`](../src/SharedMemoryStore/Diagnostics/) | Snapshot construction and failure counters | Snapshot fields and `GetFailureCount` behavior are public contracts |
| [`src/SharedMemoryStore/Lifecycle/`](../src/SharedMemoryStore/Lifecycle/) | Store operation gate for disposal-safe public boundaries | Public post-disposal outcomes are contracts |
| [`src/SharedMemoryStore/Interop/`](../src/SharedMemoryStore/Interop/) | Platform resource names, memory-mapped region adapters, and shared synchronization adapters for Linux and Windows | Platform behavior is a documented compatibility contract |
| [`src/SharedMemoryStore/Options/`](../src/SharedMemoryStore/Options/) | Option validation and detailed validation results | Public option names and validation status are contracts |

## Storage Model

The mapped region contains a header, key index, lease registry, slot metadata,
descriptor storage, and payload storage. Capacity is fixed by
`SharedMemoryStoreOptions` at create/open time. `CalculateRequiredBytes`
derives the minimum region length from slot count, value length, descriptor
length, key length, and lease-record count.

Keys, descriptors, and payloads are byte sequences. The layout does not encode
application schemas. The consumer may place frame metadata in descriptor bytes
or payload headers, but the core package remains schema-neutral.

## Slot Lifecycle

A slot moves through free, publishing, published, pending removal, and reclaim
paths. Reuse advances lifecycle identity so stale tokens do not regain access
after rollover. Maintainers must preserve the generation plus reuse-epoch
checks described in the
[disposal and rollover contract](../specs/004-store-reliability-hardening/contracts/disposal-rollover-contract.md).

Current implementation detail: slot selection and key-index compaction are
synchronous inside mutation paths. That detail does not create a public
maintenance API guarantee, but the public guarantee is that the package does not
start hidden background work.

## Key Index

The key index uses fixed-capacity open addressing with tombstones. Tombstones
preserve probe chains after removal. Diagnostics expose occupied, tombstone,
empty, usable, probe-length, and compaction counts so consumers can distinguish
key churn from live slot pressure.

The current compaction threshold is an implementation detail documented in
[Performance scope](performance.md) because it affects maintainer reasoning.
Changing it requires test and benchmark review, not a public API change by
itself.

## Lease Model

`ValueLease` is a struct token that references a slot lifecycle identity and an
active lease record. Leases protect readers from slot reuse. Release is
explicit and status-returning; dispose is best-effort.

Maintainers must preserve these invariants:

- no read span is exposed unless the slot is still published and lifecycle
  identity matches.
- removal with active leases returns `RemovePending`.
- final release can reclaim a pending-removal slot.
- repeated release returns deterministic statuses.
- explicit recovery never reclaims records owned by another live process.

## Reservation Model

`ValueReservation` is a struct token for pending direct ingest. A reservation
announces payload length and descriptor bytes before payload writes. The
producer writes to `GetSpan()` or trusted direct-I/O memory from
`DangerousGetMemory()`, records exact progress with `Advance()`, and publishes
only through `Commit()` after exact completion.

Pending reservations are invisible to readers but occupy capacity and block
duplicate keys. Abort, dispose, and recovery remove the pending index entry
before reclaiming the slot. Public memory lifetime rules are in the
[reservation memory contract](../specs/005-api-production-readiness/contracts/reservation-memory-contract.md).

## Synchronization and Waits

Public operations synchronize through the platform store lock and the
process-local lifecycle gate. Windows uses named synchronization. Linux uses a
deterministic shared lock resource in the runtime shared-memory location.
`StoreWaitOptions` controls how long an operation waits for shared
synchronization. Busy and canceled waits return `StoreBusy` or
`OperationCanceled`.

Do not add hidden worker threads or implicit global state to avoid contention.
Callers choose retry, cancellation, health check, and backoff policy.

## Recovery

Recovery is explicit and owner-scoped:

- `TryRecoverLeases` scans lease records and reports recovered, active,
  unsupported, and failed records.
- `TryRecoverReservations` scans pending reservations and reports recovered,
  active, unsupported, and failed records.

Recovery exists to make cleanup policy observable. It must not become automatic
background reclamation.

## Diagnostics

Diagnostics are snapshots, not a telemetry pipeline. `DiagnosticsSnapshot`
captures capacity, slot state, lease and reservation activity, recovery
results, key-index health, last failure, and per-status counters. Consumers own
formatting and export.

When adding public statuses or changing failure accounting, update
`docs/diagnostics.md`, `docs/errors.md`, tests under
[`tests/SharedMemoryStore.ContractTests/`](../tests/SharedMemoryStore.ContractTests/),
and `scripts/validate-docs.ps1`.

## Performance Model

The design aims to keep hot-path managed allocation low after initialization and
warm-up. Public performance wording must remain evidence-bounded and tied to
benchmark commands or measured validation notes. See
[Performance scope](performance.md) and
[`benchmarks/SharedMemoryStore.Benchmarks/`](../benchmarks/SharedMemoryStore.Benchmarks/).

## Portability Model

Current validation is `.NET 10` on Linux, Windows, and the supported same-host
Docker profile. The layout is written so future C++ and Python implementations
can conform, but no current bindings are delivered. Do not use architecture
docs to imply cross-host, macOS, Windows-container, persistence, or distributed
cache support beyond [Portability](portability.md).

## Review Invariants

Before approving a change to storage, lifecycle, synchronization, diagnostics,
recovery, public APIs, or package metadata, answer:

- Which public contract does this touch?
- Does it change a public status, method, option, layout field, package
  metadata field, or compatibility promise?
- Which docs and sample READMEs must change?
- Which contract, unit, integration, package, and documentation validations
  must pass?
- Does the wording accidentally promise persistence, distributed-cache
  semantics, hidden background work, unsupported platforms, or delivered future
  language bindings?
