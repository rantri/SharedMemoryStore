# Diagnostics

SharedMemoryStore exposes caller-owned diagnostics through `GetDiagnostics()`
and `TryGetDiagnostics()`. The public API contract defines the diagnostic
snapshot in
[public-api.md](../specs/001-frame-memory-store/contracts/public-api.md).
Key-index health follows
[index-health-contract.md](../specs/004-store-reliability-hardening/contracts/index-health-contract.md),
and observability boundaries follow
[diagnostics-integration-contract.md](../specs/005-api-production-readiness/contracts/diagnostics-integration-contract.md).

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
- `ActiveReservationCount`: slots reserved but not committed.
- `AbortedReservationCount`: reservations aborted through this handle.
- `RecoveredLeaseCount`, `ActiveLeaseRecoveryCount`,
  `UnsupportedLeaseRecoveryCount`, and `FailedLeaseRecoveryCount`: lease
  recovery outcomes.
- `RecoveredReservationCount`, `ActiveReservationRecoveryCount`,
  `UnsupportedReservationRecoveryCount`, and
  `FailedReservationRecoveryCount`: reservation recovery outcomes.
- `CapacityPressureCount`: store-full and lease-table-full pressure failures.
- `IndexEntryCount`, `OccupiedIndexEntryCount`,
  `TombstoneIndexEntryCount`, `EmptyIndexEntryCount`,
  `TombstonePressureRatio`, `UsableIndexCapacity`,
  `LastObservedProbeLength`, `MaxObservedProbeLength`, and
  `IndexCompactionCount`: key-index health and churn signals.
- `LastFailureStatus`: last non-success operation status observed by the
  handle.
- `GetFailureCount(StoreStatus status)`: aggregate per-status failure counts
  including validation, contention, cancellation, lifecycle, capacity, platform,
  reservation, lease, and unexpected failures.

## Example

```csharp
var status = store.TryGetDiagnostics(out var snapshot);
if (status == StoreStatus.Success)
{
    logger.LogInformation(
        "SharedMemoryStore free={Free} published={Published} activeLeases={Leases} lastFailure={LastFailure}",
        snapshot.FreeSlotCount,
        snapshot.PublishedSlotCount,
        snapshot.ActiveLeaseCount,
        snapshot.LastFailureStatus);
}
```

Use `GetFailureCount(StoreStatus status)` when exporting metrics by status:

```csharp
var fullFailures = snapshot.GetFailureCount(StoreStatus.StoreFull);
var busyFailures = snapshot.GetFailureCount(StoreStatus.StoreBusy);
```

## Troubleshooting Signals

- Rising `CapacityPressureCount` indicates slot or lease-record pressure.
- Nonzero `GetFailureCount(StoreStatus.StoreFull)` points to slot pressure.
- Nonzero `GetFailureCount(StoreStatus.LeaseTableFull)` points to concurrent
  reader or leaked lease pressure.
- Nonzero `GetFailureCount(StoreStatus.RemovePending)` indicates removals while
  readers are holding leases.
- Nonzero `GetFailureCount(StoreStatus.InvalidLease)` or
  `GetFailureCount(StoreStatus.LeaseAlreadyReleased)` indicates lease ownership
  or disposal paths need review.
- Nonzero reservation failure counts indicate a producer advanced, committed,
  aborted, disposed, or recovered a reservation outside the expected lifecycle.
- Nonzero recovery result counters identify recovered records, live-owner skips,
  unsupported owner checks, and unsafe records.
- Rising tombstone counts with low occupied counts indicate key churn pressure.
  Internal compaction is synchronous and caller-triggered by mutation paths; the
  library does not start a background maintenance worker.
- `GetFailureCount(StoreStatus.UnsupportedPlatform)` indicates an unsupported
  OS, restricted host, isolated Docker profile, or recovery capability mismatch.
  On Linux, Windows, and supported same-host Docker profiles, equivalent
  workloads should report the same diagnostic categories.
- `GetFailureCount(StoreStatus.CorruptStore)` means the process should stop
  unsafe access and gather evidence for maintainers.

## Support Evidence

When reporting a bug, include:

- package version and target framework.
- OS, architecture, and .NET runtime.
- store options without secrets.
- operation status and whether the operation used `StoreWaitOptions`.
- relevant `DiagnosticsSnapshot` fields.
- sample command or minimal reproduction.

Do not include secrets or payload bytes unless maintainers explicitly request a
safe minimal reproduction. Use [SUPPORT.md](../SUPPORT.md) for public reports
and [SECURITY.md](../SECURITY.md) for private vulnerability reporting.
