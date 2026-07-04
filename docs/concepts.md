# Concepts

SharedMemoryStore is a bounded named shared-memory key-value store for opaque
binary values. This page defines the vocabulary used by the rest of the
documentation before the advanced workflows introduce reservations, recovery,
diagnostics, and portability details.

Behavior claims on this page trace to the
[public API contract](../specs/001-frame-memory-store/contracts/public-api.md),
the
[shared-memory layout contract](../specs/001-frame-memory-store/contracts/shared-memory-layout.md),
and the
[reservation API contract](../specs/003-zero-copy-ingest/contracts/reservation-api.md).

## Store

A store is one named memory-mapped region opened through `MemoryStore`. It owns
fixed capacity for keys, descriptors, payload bytes, lease records, and shared
metadata. A `MemoryStore` instance is a disposable process-local handle to that
region, not an application service container.

Use a store when trusted same-host processes need to exchange immutable payload
bytes without copying through a broker process. Do not use it as a network
cache, persistent database, message broker, or schema parser.

## Name

`SharedMemoryStoreOptions.Name` is the operating-system-visible mapping name.
Consumers should choose stable names for shared stores and unique names for
tests and samples. Names are part of deployment configuration; they are not
keys inside the store.

## Key

A key is an opaque byte sequence used for exact lookup. The package does not
interpret strings, encodings, paths, or frame identifiers. If an application
uses strings, it must encode and decode them outside the store. Keys must be
non-empty and no larger than `MaxKeyBytes`; invalid or oversized keys return
`InvalidKey` or `KeyTooLarge`.

Use canonical, stable encodings for application keys. Prefer fixed-width
binary for numeric keys, UTF-8 for text keys, and explicit byte order for data
shared across language boundaries. See [Byte encoding](byte-encoding.md) for
allocation-conscious helpers and composite key guidance.

## Descriptor

A descriptor is optional opaque metadata stored beside the payload. Descriptors
are useful for consumer-owned shape information such as frame dimensions,
timestamps, content type, or version bytes. The core package enforces only the
configured `MaxDescriptorBytes`; it does not parse descriptor content.

Descriptors should stay compact and structured. The package stores them as
bytes and returns them through `ValueLease.DescriptorSpan`; consumers own any
schema, versioning, and decoding convention.

## Payload

A payload is the immutable value byte sequence readers acquire. Payloads are
stored in fixed-size slots with maximum length `MaxValueBytes`. Values published
through `TryPublish`, `TryReserve` plus `Commit`, or `TryPublishSegments` become
ordinary immutable payload bytes once publication succeeds.

If payload bytes already exist, pass them directly. If producing a temporary
full array would be wasteful, use direct reservation ingest or segmented
publish.

## Slot

A slot is reusable storage for one descriptor and one payload. `SlotCount`
controls how many values can be published or pending removal/reservation at one
time. Slots carry lifecycle identity so a stale lease or reservation token does
not become valid after reuse.

Capacity pressure is usually slot pressure, lease-record pressure, or key-index
churn. See [Diagnostics](diagnostics.md) and [Performance scope](performance.md)
for the fields that distinguish those cases.

## Lease

A `ValueLease` protects one published slot generation while a reader examines
`DescriptorSpan` and `ValueSpan`. Release the lease exactly once with
`Release()` when the return status matters, or dispose it for best-effort
cleanup. Removing a leased value returns `RemovePending` until the final lease
releases.

Read spans are valid only while the lease remains active and the store handle
remains open.

## Reservation

A `ValueReservation` is a producer token for direct ingest into store-owned
payload memory. The producer announces the final payload length and descriptor,
writes into `GetSpan()`, records progress with `Advance(int)`, and publishes
atomically with `Commit()`. Readers cannot acquire the key until commit
succeeds.

Use `Abort()` or dispose the reservation when the producer cannot finish.
Incomplete reservations can also be recovered explicitly by an owner. The
reservation layout is described by
[ingest layout](../specs/003-zero-copy-ingest/contracts/ingest-layout.md) and
the public memory lifetime rules are described by
[reservation memory](../specs/005-api-production-readiness/contracts/reservation-memory-contract.md).

## Segmented Publish

`TryPublishSegments` accepts a `ReadOnlySequence<byte>` and publishes it as one
contiguous immutable store value. It is for payloads that already exist in
segments, such as parser or pipeline buffers. It does not make scatter/gather
storage part of the public shared-memory layout; the committed value is still
one contiguous slot payload.

## Wait Policy

Public operations use `StoreWaitOptions` to control shared synchronization.
`Default` waits for a bounded time, `NoWait` returns `StoreBusy` immediately
when synchronization is unavailable, and `Infinite` is available only for
callers that intentionally accept unbounded waits. Cancellation returns
`OperationCanceled`. Wait behavior is governed by the
[contention configuration contract](../specs/005-api-production-readiness/contracts/contention-configuration-contract.md).

## Status

Expected outcomes are returned as `StoreOpenStatus` or `StoreStatus`, not by
throwing for normal pressure, lookup, validation, contention, or lifecycle
cases. `Success` means the requested operation completed. Non-success statuses
are documented in [Errors and statuses](errors.md).

## Diagnostics Snapshot

`DiagnosticsSnapshot` is an allocation-conscious snapshot returned by
`GetDiagnostics()` or `TryGetDiagnostics`. It includes capacity counts, active
lease and reservation counts, recovery results, key-index health, last failure,
and per-status failure counts through `GetFailureCount(StoreStatus)`.

The package does not format logs, export metrics, or run background
observability workers. Callers own that integration.

## Recovery

Recovery is explicit and owner-controlled. `TryRecoverLeases` can reclaim stale
lease records when `EnableLeaseRecovery` permits it. `TryRecoverReservations`
can remove pending reservation keys and reclaim slots when a producer is no
longer active. Recovery reports recovered, active, unsupported, and failed
records so callers can make policy decisions.

Recovery is not a replacement for normal `Release()`, `Abort()`, and `Dispose()`
paths.

## Capacity Pressure

Capacity is fixed by options at create/open time. Pressure can come from:

- too few reusable slots for published, pending-removal, and pending-reservation
  values.
- too few lease records for concurrent readers.
- oversized keys, descriptors, or payloads.
- key-index tombstones from high churn.

Use diagnostics before increasing capacity so the change addresses the right
resource.

## Lifecycle

The lifecycle is explicit: create or open a store, publish or reserve values,
acquire and release leases, remove values, recover only when policy allows it,
and dispose store handles. See [Lifecycle](lifecycle.md) for detailed ownership
rules and abnormal termination behavior.

## Portability

The current package is C# on `.NET 10` with Linux and Windows host support and
same-host Docker support for configured Linux containers. C++ and Python are
future portability audiences, not current bindings. Future implementations must
follow the documented layout and lifecycle contracts rather than redefining
behavior per language. See [Portability](portability.md).

## Package Contract

Public APIs, status names, package metadata, shared-memory layout, lifecycle
rules, diagnostics semantics, and documented compatibility promises are package
contracts. Current implementation details can be explained for maintainers, but
they are not automatically compatibility guarantees. See
[Maintainers](maintainers.md) for review rules.
