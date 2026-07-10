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
- [Language-neutral protocol](../protocol/README.md)
- [Native C ABI contract](../specs/008-cpp-python-implementations/contracts/native-c-api.md)
- [C++ API contract](../specs/008-cpp-python-implementations/contracts/cpp-api.md)
- [Python API contract](../specs/008-cpp-python-implementations/contracts/python-api.md)
- [Interoperability contract](../specs/008-cpp-python-implementations/contracts/interoperability.md)

## Responsibility Boundary

The repository delivers independently consumable .NET, native C++, and Python
libraries. Their common responsibility is to provide a bounded named
shared-memory store for opaque byte keys, optional descriptor bytes, immutable
payload bytes, leases, direct reservations, segmented publish, explicit
recovery, and diagnostics through one layout and platform-resource protocol.

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
| [`protocol/`](../protocol/) | Canonical layout `1.2`, resource naming `1`, compatibility metadata, and conformance fixtures | Language-neutral bytes, names, states, hashes, and version identities are contracts |
| [`src/cpp/include/shared_memory_store/c_api.h`](../src/cpp/include/shared_memory_store/c_api.h) | Fixed-width C ABI `1.0`, versioned structures, statuses, and opaque handles | Exported names, widths, status numbers, ownership, and lifetime rules are ABI contracts |
| [`src/cpp/include/shared_memory_store/store.hpp`](../src/cpp/include/shared_memory_store/store.hpp) | Move-only C++20 RAII stores, leases, reservations, spans, reports, and diagnostics | Public C++ surface and status behavior follow the C++ distribution version |
| [`src/cpp/src/`](../src/cpp/src/) | Native protocol algorithms plus Windows and Linux mapping, lock, ownership, and cleanup adapters | Algorithms may change only while mapped and platform-resource contracts remain compatible |
| [`src/python/shared_memory_store/`](../src/python/shared_memory_store/) | Python enums and context-managed wrappers over the packaged C ABI through `ctypes` | Python public names, result shapes, view ownership, and loader policy follow the Python distribution version |
| [`tests/SharedMemoryStore.InteropTests/`](../tests/SharedMemoryStore.InteropTests/) | JSON-lines agents and ordered runtime-pair orchestration | Test protocol is test-only; observed cross-runtime behavior is release evidence |

## Dependency Direction

```text
Python API -> ctypes declarations -> C ABI -> C++ protocol core -> OS adapter
C++ RAII API ------------------------^             |
.NET implementation ------------------------------+-> protocol fixtures
interop agents -> public APIs only
```

The C ABI does not depend on Python and never exposes exceptions, C++ standard
library types, platform-sized lengths, or allocator ownership. Python loads the
native library bundled beside its modules and validates ABI, layout, record
sizes, and resource-naming identities before use. The .NET implementation
remains independent of the native ABI; both implementations depend on the
canonical protocol.

## Storage Model

The mapped region contains a header, key index, lease registry, slot metadata,
descriptor storage, and payload storage. Capacity is fixed at create/open time.
Each API exposes the canonical capacity calculation from slot count, value
length, descriptor length, key length, and lease-record count. Exact records,
offsets, state numbers, and arithmetic are pinned in
[`protocol/layout-v1.2.md`](../protocol/layout-v1.2.md) and its fixtures.

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

A lease token references a slot lifecycle identity and an active lease record.
The managed `ValueLease`, C++ `value_lease`, and Python `ValueLease` protect
readers from slot reuse. Release is explicit and status-returning; disposal,
destruction, and finalization are best-effort fallbacks appropriate to each
runtime.

Maintainers must preserve these invariants:

- no read span is exposed unless the slot is still published and lifecycle
  identity matches.
- removal with active leases returns `RemovePending`.
- final release can reclaim a pending-removal slot.
- repeated release returns deterministic statuses.
- explicit recovery never reclaims records owned by another live process.

## Reservation Model

A reservation token represents pending direct ingest. The managed
`ValueReservation`, C++ `value_reservation`, and Python `ValueReservation`
announce payload length and descriptor bytes before payload writes, expose
runtime-appropriate writable views, record exact progress, and publish only
after an exact commit.

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

The native adapters reproduce the same Windows mapping/mutex and Linux region,
byte-range lock, owner-sidecar, lifecycle-lock, permission, and cleanup rules.
Matching mapped bytes without matching resource participation is not
interoperability.

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

The repository now contains managed, C++20, and Python 3.10+ implementations
targeting 64-bit little-endian Linux and Windows. Distribution presence is not
the same as release validation: native tests, wheel installation, clean CMake
consumption, Windows/Linux checks, and the required ordered runtime-pair matrix
must be recorded for each release. Do not imply cross-host, macOS,
Windows-container, persistence, or distributed-cache support beyond
[Portability](portability.md).

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
  semantics, hidden background work, unsupported platforms, registry
  publication, or interoperability evidence that was not actually run?
