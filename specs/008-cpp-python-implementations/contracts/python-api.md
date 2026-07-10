# Contract: Python API

Package `shared_memory_store` exposes Pythonic lifetime wrappers over the native
C ABI with no third-party runtime dependency.

## Public Types

- `OpenMode`, `StoreOpenStatus`, and `StoreStatus` as `IntEnum` values identical
  to the shared numeric contract.
- Immutable or keyword-oriented `StoreOptions`, `WaitOptions`, recovery reports,
  and diagnostics snapshots.
- Context-managed `MemoryStore`, `ValueLease`, and `ValueReservation`.

## Operations

- `calculate_required_bytes(...)` and `StoreOptions.create(...)`.
- `MemoryStore.open(options, wait=...)` returns `(status, store_or_none)`.
- Store methods mirror publish, segmented publish, acquire, remove, reserve,
  recovery, and diagnostics using explicit status results.
- `ValueLease.value` and `.descriptor` return read-only zero-copy `memoryview`
  objects tied to the lease lifetime.
- A reservation exposes a writable zero-copy `memoryview` for its remaining
  range plus `advance`, `commit`, and `abort`.
- Closing a context is idempotent. Finalizers are best-effort fallbacks and are
  not the primary resource-management contract.

## Input Rules

Keys, descriptors, and payloads accept bytes-like objects and preserve exact
bytes. Store names are Python strings encoded as strict UTF-8. Mutable buffers
passed for immediate publication may be copied into the store before the call
returns; borrowed shared-memory views never outlive their owning token.

## Native Loading

The package loads only the shared library shipped beside its Python modules.
It does not search arbitrary current-working-directory libraries. Loader errors
identify the expected platform artifact and package location without printing.
