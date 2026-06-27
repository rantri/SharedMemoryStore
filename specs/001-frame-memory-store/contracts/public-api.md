# Public API Contract

## Package

- Package id: `SharedMemoryStore`
- Target framework: `net10.0`
- Root namespace: `SharedMemoryStore`
- Runtime dependencies: .NET BCL only
- Semantic version impact: initial public package contract. Before a stable
  `1.0.0`, breaking changes must be documented in feature and migration notes.
  At or after `1.0.0`, breaking public API, layout, or behavior changes require
  a major version bump.

## Core Types

### `SharedMemoryStoreOptions`

Configuration supplied during create/open.

Required members:
- `Name`
- `OpenMode`: `CreateNew`, `OpenExisting`, or `CreateOrOpen`
- `TotalBytes`
- `SlotCount`
- `MaxValueBytes`
- `MaxDescriptorBytes`
- `MaxKeyBytes`
- `LeaseRecordCount`
- `EnableLeaseRecovery`

Validation failures return or throw during initialization only. Hot-path store
operations must not validate options repeatedly.

### `SharedMemoryStore`

Disposable owner of one mapped store handle.

Required members:
- `static StoreOpenStatus TryCreateOrOpen(in SharedMemoryStoreOptions options, out SharedMemoryStore? store)`
- `StoreStatus TryPublish(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, ReadOnlySpan<byte> descriptor = default)`
- `StoreStatus TryAcquire(ReadOnlySpan<byte> key, out ValueLease lease)`
- `StoreStatus TryRemove(ReadOnlySpan<byte> key)`
- `StoreStatus TryRecoverLeases(in LeaseRecoveryOptions options, out LeaseRecoveryReport report)`
- `DiagnosticsSnapshot GetDiagnostics()`
- `void Dispose()`

Contract rules:
- after initialization and warm-up, `TryPublish`, `TryAcquire`, `TryRemove`,
  lease release, reuse, and diagnostic snapshot retrieval must not allocate
  managed heap memory per operation.
- expected operational failures return `StoreStatus` values rather than throwing.
- methods are thread-safe for concurrent producers and consumers according to
  the documented state machine.
- disposing the store invalidates future operations and all spans previously
  obtained from leases.

### `ValueLease`

Struct token returned by successful acquire.

Required members:
- `bool IsValid`
- `int ValueLength`
- `int DescriptorLength`
- `ReadOnlySpan<byte> ValueSpan`
- `ReadOnlySpan<byte> DescriptorSpan`
- `StoreStatus Release()`
- `void Dispose()`

Contract rules:
- `Release()` succeeds at most once.
- `Dispose()` releases when the lease is active; callers that need the status
  use `Release()`.
- spans are valid only while the lease is active and the store is open.
- holding a lease prevents the slot generation from being reused.
- value and descriptor spans are read-only.

### `DiagnosticsSnapshot`

Small snapshot struct for consumer-controlled diagnostics.

Required members:
- configured capacity and slot counts.
- free, published, pending removal, and active lease counts.
- operation failure counters grouped by status.
- capacity pressure indicators.

Contract rules:
- library code never writes diagnostics directly to the console.
- consumers own formatting, logging, metrics export, and alerting.

## Allocation Contract

Allowed allocations:
- create/open initialization.
- mapping creation and disposal.
- optional caller-owned convenience APIs that explicitly document allocation.
- tests, samples, and diagnostic formatting outside hot-path APIs.

Disallowed after warm-up for core APIs:
- per-publish heap allocation.
- per-acquire heap allocation.
- per-release heap allocation.
- per-remove heap allocation.
- allocation during slot reuse.
- hidden background work that allocates on behalf of callers.

## Payload and Descriptor Contract

- payload bytes are opaque to the core store.
- descriptor bytes are optional and opaque to the core store.
- frames are represented by consumer-defined payload and descriptor layout.
- the core store does not parse frame headers, metadata, or payload sections.
- a non-frame value has identical lifecycle, lease, and reuse behavior.

## Threading and Process Contract

- all shared counters and state transitions use aligned atomic operations.
- readers may concurrently acquire the same published value.
- remove can race with acquire; exactly one documented outcome is returned.
- storage is not reusable until usage count reaches zero.
- stale lease recovery is explicit and owner-controlled.
- no process-wide global configuration is required.
