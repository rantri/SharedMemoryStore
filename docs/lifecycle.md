# Lifecycle

SharedMemoryStore owns one handle to a named memory-mapped region. The public
API lifecycle is defined by the
[public API contract](../specs/001-frame-memory-store/contracts/public-api.md),
the status outcomes are defined by the
[error taxonomy contract](../specs/001-frame-memory-store/contracts/error-taxonomy.md),
and the shared layout state machine is defined by the
[shared-memory layout contract](../specs/001-frame-memory-store/contracts/shared-memory-layout.md).

## Roles

- Store owner: creates or opens the mapping, chooses capacity limits, enables or
  disables explicit lease recovery, and disposes the handle.
- Producer: publishes immutable payload bytes and optional descriptor bytes
  under an opaque byte key.
- Reader: acquires a `ValueLease`, reads descriptor and value spans, and
  releases or disposes the lease exactly once.
- Maintainer: updates contracts and release notes when lifecycle behavior
  changes.

Published values are immutable. Removing an unleased value reclaims the slot
immediately. Removing a leased value returns `RemovePending`; the slot remains
protected until the final active lease releases, then the slot generation is
advanced and storage becomes reusable.

## Lease Ownership

A lease protects one slot generation. Holding the lease prevents the removed
slot from being reused for another value. The lease spans are valid only while
the lease is active and the store handle remains open.

Call `Release()` when the return status matters. `Dispose()` is useful for
best-effort cleanup paths where the caller does not need the release status.
Repeated release returns a deterministic status instead of silently succeeding.

## Removal and Reuse

Removal is key-based. If no lease protects the slot, the key is removed and the
slot can be reused immediately. If readers still hold leases, removal records a
pending state. New acquires for that key fail, active readers can finish, and
the final release reclaims the slot.

Stale lease recovery is explicit and owner controlled through
`TryRecoverLeases`. The library never starts background cleanup and never writes
diagnostics to the console. Callers own logging, metrics formatting, and recovery
policy.

Use recovery for controlled owner policy, process-liveness cleanup, or tests
that intentionally recover current-process leases. Normal consumers should still
release or dispose leases directly.

## Abnormal Termination

If a process terminates while holding a lease, the shared lease record can remain
active until an owner explicitly runs recovery. Platforms without reliable
owner-liveness checks return deterministic unsupported statuses. The store does
not run background reclamation threads.

If a process terminates while publishing or reclaiming, later operations validate
shared state before exposing payload spans. Impossible state transitions move
the store toward safe error outcomes such as `CorruptStore`.

Frame-shaped data is represented as ordinary descriptor and value bytes. The
core store does not parse frame headers, metadata, payload sections, or any
other application-specific schema.

Expected operational failures return `StoreStatus` values: duplicate keys,
missing keys, oversized inputs, full stores, invalid leases, repeated release,
pending removal, unsupported platforms, disposed stores, access failures, and
corruption-safe mode.

## Cleanup Responsibilities

- Dispose every store handle.
- Release or dispose every successful `ValueLease`.
- Avoid retaining span references after release or store disposal.
- Record diagnostics before disposing when troubleshooting a failure.
- Use `TryRecoverLeases` only when the owner policy permits recovery.
