# Diagnostics

SharedMemoryStore exposes caller-owned diagnostics through `GetDiagnostics()`.
The public API contract defines the diagnostic snapshot as part of the
[public API contract](../specs/001-frame-memory-store/contracts/public-api.md),
and non-success status counters follow the
[error taxonomy contract](../specs/001-frame-memory-store/contracts/error-taxonomy.md).

The library does not write to the console, configure logging, export metrics, or
start hidden background work. Consumers decide how snapshots become logs,
metrics, traces, alerts, or support evidence.

## Snapshot Fields

`DiagnosticsSnapshot` includes:

- `TotalBytes`: configured mapped-region length.
- `SlotCount`: configured reusable slot count.
- `FreeSlotCount`: slots currently available for publish.
- `PublishedSlotCount`: slots currently published.
- `PendingRemovalCount`: slots waiting for final lease release.
- `ActiveLeaseCount`: active lease records.
- `ActiveReservationCount`: slots currently reserved but not committed.
- `AbortedReservationCount`: reservations aborted through this handle.
- `FailedCommitCount`: incomplete reservation commit attempts.
- `RecoveredReservationCount`: stale reservations recovered through this handle.
- `ActiveReservationRecoveryCount`: reservations observed as still active during
  explicit recovery scans.
- `UnsupportedReservationRecoveryCount`: reservations that recovery could not
  evaluate safely on the current platform.
- `FailedReservationRecoveryCount`: reservations recovery could not reclaim
  because slot or index state was inconsistent.
- `CapacityPressureCount`: count of store-full and lease-table-full failures.
- `LastFailureStatus`: last non-success operation status observed by the
  handle.
- per-status failure counters for duplicate key, missing key, oversized inputs,
  store full, lease table full, invalid lease, repeated release, pending
  removal, unsupported platform, disposed store, corrupt store, access denied,
  unknown failure, invalid reservation, incomplete reservation, repeated
  reservation completion, and out-of-range reservation writes.

## Example

```csharp
var snapshot = store.GetDiagnostics();

logger.LogInformation(
    "SharedMemoryStore slots free={Free} published={Published} activeLeases={Leases} lastFailure={LastFailure}",
    snapshot.FreeSlotCount,
    snapshot.PublishedSlotCount,
    snapshot.ActiveLeaseCount,
    snapshot.LastFailureStatus);
```

Use `GetFailureCount(StoreStatus status)` when exporting metrics by status:

```csharp
var fullFailures = snapshot.GetFailureCount(StoreStatus.StoreFull);
```

## Troubleshooting Signals

- Rising `CapacityPressureCount` indicates slot or lease-record pressure.
- Nonzero `RemovePendingFailures` indicates readers are holding leases while
  removals are requested.
- Nonzero `LeaseAlreadyReleasedFailures` or `InvalidLeaseFailures` indicates
  lease ownership or disposal paths need review.
- Nonzero reservation failure counters indicate a producer advanced, committed,
  aborted, disposed, or recovered a reservation outside the expected lifecycle.
- Nonzero reservation recovery result counters identify whether recovery found
  live owners, unsupported owner-liveness checks, or inconsistent shared state.
- `UnsupportedPlatformFailures` indicates a platform or recovery capability
  mismatch.
- `CorruptStoreFailures` means the process should stop unsafe access and gather
  evidence for maintainers.

## Support Evidence

When reporting a bug, include the package version, OS, .NET runtime, store
options, operation status, and relevant diagnostic snapshot fields. Do not
include secrets or payload bytes unless maintainers explicitly request a safe
minimal reproduction.
