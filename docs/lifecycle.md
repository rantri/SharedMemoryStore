# Lifecycle

SharedMemoryStore owns one handle to a named memory-mapped region. The public
API lifecycle is defined by
[public-api.md](../specs/001-frame-memory-store/contracts/public-api.md), status
outcomes are defined by
[error-taxonomy.md](../specs/001-frame-memory-store/contracts/error-taxonomy.md),
shared layout state is defined by
[shared-memory-layout.md](../specs/001-frame-memory-store/contracts/shared-memory-layout.md),
owner recovery is defined by
[owner-recovery-contract.md](../specs/004-store-reliability-hardening/contracts/owner-recovery-contract.md),
and disposal/rollover behavior is defined by
[disposal-rollover-contract.md](../specs/004-store-reliability-hardening/contracts/disposal-rollover-contract.md).

## Roles

- Store owner: creates or opens the mapping, chooses capacity limits, enables or
  disables explicit lease recovery, and disposes the handle.
- Producer: publishes immutable payload bytes and optional descriptor bytes
  under an opaque byte key through `TryPublish`, `TryReserve`, or
  `TryPublishSegments`.
- Reader: acquires a `ValueLease`, reads descriptor and value spans, and
  releases or disposes the lease exactly once.
- Maintainer: updates contracts, docs, samples, tests, and release notes when
  lifecycle behavior changes.

## Store Handle

`MemoryStore` is a disposable process-local handle. Disposing one handle does
not make another process-local handle disappear, but the disposed handle must no
longer be used. Operations after disposal return `StoreDisposed` or token-level
invalid outcomes instead of exposing internal disposal exceptions.

## Published Value

A published value is immutable. Removing an unleased value reclaims its slot
immediately. Removing a leased value returns `RemovePending`; the slot remains
protected until the final active lease releases, then storage becomes reusable.

## Lease Ownership

A lease protects one slot generation and reuse epoch. Holding the lease prevents
the removed slot from being reused for another value. The lease spans are valid
only while the lease is active and the store handle remains open.

Call `Release()` when the return status matters. `Dispose()` is useful for
best-effort cleanup paths where the caller does not need the release status.
Repeated release returns deterministic statuses.

## Reservation Ownership

A reservation owns one slot generation while its state is pending publication.
During that period the key is present for duplicate detection, but `TryAcquire`
returns `NotFound`. The producer may write only into the remaining payload
region returned by `GetSpan()` and must call `Advance()` with the exact number
of bytes written.

`Commit()` publishes the value only when progress equals the announced payload
length. `Abort()` removes the pending key before reclaiming the slot. Disposing
an active reservation aborts it; completing a reservation more than once returns
deterministic statuses. The memory lifetime rules are covered by
[reservation-memory-contract.md](../specs/005-api-production-readiness/contracts/reservation-memory-contract.md).

## Reader and Producer Rules

Readers:

- treat descriptor and payload spans as read-only and short-lived.
- release or dispose every successful `ValueLease`.
- do not retain spans after release or store disposal.

Producers:

- choose byte keys deterministically.
- publish only payloads and descriptors within configured maxima.
- commit, abort, dispose, or recover every reservation.
- handle `DuplicateKey`, `StoreFull`, `StoreBusy`, and cancellation outcomes
  through caller-owned policy.

## Explicit Recovery

`TryRecoverLeases` is owner controlled. When `RecoverCurrentProcessLeases` is
`true`, the store may recover current-process leases and stale-owner leases. It
must still skip leases owned by another live process. Reports include scanned,
recovered, active, unsupported, and failed counts.

`TryRecoverReservations` scans pending reservations, evaluates producer liveness
where supported, removes pending index entries, and reclaims slots without
exposing payload bytes. Current-process reservation recovery is for tests and
controlled shutdown paths.

Recovery is not automatic and is not a replacement for ordinary release, abort,
and dispose paths.

## Abnormal Termination

If a process terminates while holding a lease, the shared lease record can
remain active until an owner explicitly runs recovery. Platforms without
reliable owner-liveness checks report unsupported counts rather than unsafe
cleanup.

If a process terminates while holding a reservation, the pending key remains
invisible to readers and occupies capacity until an owner aborts or recovers it.

If a process terminates while publishing or reclaiming, later operations
validate shared state before exposing payload spans. Impossible transitions move
the store toward safe error outcomes such as `CorruptStore`.

## Cleanup Responsibilities

- Dispose every store handle.
- Release or dispose every successful `ValueLease`.
- Commit, abort, dispose, or recover every pending `ValueReservation`.
- Avoid retaining span references after release, abort, commit, recovery, or
  store disposal.
- Record diagnostics before disposal when troubleshooting a failure.
- Use `TryRecoverLeases` and `TryRecoverReservations` only when owner policy
  permits recovery.

## Long-Running Identity

Reusable slots carry generation and reuse-epoch identity. Index entries, lease
records, lease tokens, and reservation tokens compare the full identity before
exposing memory or reclaiming storage. When a generation reaches its integer
boundary, generation returns to `1` and the reuse epoch advances, so old tokens
do not become valid again after long-running reuse cycles.

## Related Samples

- [samples/BasicUsage/README.md](../samples/BasicUsage/README.md): ordinary
  publish, acquire, release, remove, reuse, and dispose.
- [samples/FrameValue/README.md](../samples/FrameValue/README.md): multiple
  readers and `RemovePending`.
- [samples/ZeroCopyIngest/README.md](../samples/ZeroCopyIngest/README.md):
  reservation commit, abort, and reader visibility.
- [samples/HostedServiceIntegration/README.md](../samples/HostedServiceIntegration/README.md):
  startup, diagnostics, explicit recovery, and shutdown cleanup.
