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
- `CapacityPressureCount`: count of store-full and lease-table-full failures.
- `LastFailureStatus`: last non-success operation status observed by the
  handle.
- per-status failure counters for duplicate key, missing key, oversized inputs,
  store full, lease table full, invalid lease, repeated release, pending
  removal, unsupported platform, disposed store, corrupt store, access denied,
  and unknown failure.

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
- `UnsupportedPlatformFailures` indicates a platform or recovery capability
  mismatch.
- `CorruptStoreFailures` means the process should stop unsafe access and gather
  evidence for maintainers.

## Support Evidence

When reporting a bug, include the package version, OS, .NET runtime, store
options, operation status, and relevant diagnostic snapshot fields. Do not
include secrets or payload bytes unless maintainers explicitly request a safe
minimal reproduction.
