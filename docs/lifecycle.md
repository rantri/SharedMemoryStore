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
  under an opaque byte key, either through `TryPublish` or a pending
  `ValueReservation`.
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

Current-process lease recovery is owner-scoped. When
`RecoverCurrentProcessLeases` is `true`, the store may recover leases owned by
the current process and leases whose owner process is stale. It must still skip
leases owned by another live process. Skipped live-owner records are reported as
`ActiveLeaseCount`; unsupported liveness checks are reported as
`UnsupportedLeaseCount`; inconsistent shared records are reported as
`FailedRecoveryCount`.

## Reservation Ownership

A reservation owns one slot generation while its state is `SlotPublishing`.
During that period the key is present in the index for duplicate detection, but
`TryAcquire` returns `NotFound`. The producer may write only into the remaining
payload region returned by `GetSpan()` or `GetMemory()` and must call
`Advance()` with the exact number of bytes written.

`Commit()` publishes the value only when progress equals the announced payload
length. `Abort()` removes the pending key before reclaiming the slot. Disposing
an active reservation aborts it; disposing or aborting after completion is a
deterministic no-op or status-returning failure.

`TryRecoverReservations` is owner controlled. It scans pending reservations,
evaluates producer liveness where supported, removes the pending index entry,
and reclaims the slot without exposing payload bytes.

## Abnormal Termination

If a process terminates while holding a lease, the shared lease record can remain
active until an owner explicitly runs recovery. Platforms without reliable
owner-liveness checks return deterministic unsupported statuses. The store does
not run background reclamation threads.

If a process terminates while publishing or reclaiming, later operations validate
shared state before exposing payload spans. Impossible state transitions move
the store toward safe error outcomes such as `CorruptStore`.

If a process terminates while holding a reservation, the pending key remains
invisible to readers and occupies capacity until an owner explicitly aborts or
recovers it.

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
- Commit, abort, dispose, or recover every pending `ValueReservation`.
- Avoid retaining span references after release or store disposal.
- Record diagnostics before disposing when troubleshooting a failure.
- Use `TryRecoverLeases` only when the owner policy permits recovery.

## Long-Running Identity

Reusable slots carry a generation and reuse epoch. Index entries, lease records,
lease tokens, and reservation tokens compare the full identity before exposing
memory or reclaiming storage. When a generation reaches its integer boundary,
the generation returns to `1` and the reuse epoch advances, so old tokens do not
become valid again after long-running reuse cycles.
