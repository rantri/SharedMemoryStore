# Diagnostics

Diagnostics are caller-controlled, bounded observations. The store has no
background reporter and never writes diagnostics to the console. Correctness
does not depend on taking a snapshot.

The shared-versus-local comparison rules follow the current
[public API](../specs/010-lock-free-only-multilang/contracts/public-api.md) and
[interoperability](../specs/010-lock-free-only-multilang/contracts/interoperability-and-validation.md)
contracts.

C#:

```csharp
StoreStatus status = store.TryGetDiagnostics(
    new StoreWaitOptions(TimeSpan.FromMilliseconds(100)),
    out DiagnosticsSnapshot snapshot);

if (status == StoreStatus.Success)
{
    Console.WriteLine(snapshot.ProtocolInfo);
    Console.WriteLine($"slots: {snapshot.FreeSlotCount}/{snapshot.SlotCount}");
}
```

C++ uses `try_get_diagnostics`; Python uses `store.diagnostics()`. Every runtime
reports the same canonical protocol identity and equivalent shared facts.

## Protocol identity

`ProtocolInfo` is immutable for a successfully opened handle:

| Field | Current value |
|---|---:|
| Layout major | `2` |
| Layout minor | `0` |
| Resource protocol | `2` |
| Required features | `7` |
| Optional features | `0` |

Treat the five fields as one identity. Package version is not part of this
value.

## Shared structural facts

These values are derived from the mapped region and should agree across
runtimes observing a stable state:

- `TotalBytes` and `SlotCount`;
- `FreeSlotCount`, `InitializingSlotCount`, `ReservedSlotCount`,
  `PublishedSlotCount`, `PendingRemovalCount`, `ReclaimingSlotCount`, and
  `RetiredSlotCount`;
- `ActiveReservationCount`;
- `ActiveLeaseCount`, `ClaimingLeaseCount`, `RecoveringLeaseCount`,
  `FreeLeaseCount`, and `RetiredLeaseCount`;
- `ParticipantRecordCount`, `FreeParticipantCount`,
  `RegisteringParticipantCount`, `ActiveParticipantCount`,
  `ClosingParticipantCount`, `RecoveringParticipantCount`,
  `ReclaimingParticipantCount`, and `RetiredParticipantCount`;
- `IsParticipantTableExhausted`;
- aggregate directory capacity/occupancy values; and
- `PrimaryDirectoryOccupancy`, `SpilledBucketCount`, and
  `OverflowDirectoryOccupancy`.

Slot states should account for the configured slot count in a stable snapshot:

```text
free + initializing + reserved + published + pending-removal + reclaiming + retired
    = slot count
```

Participant states follow the same accounting pattern. A concurrent snapshot is
bounded and may observe legal movement between records; use repeated snapshots
when an operator needs a stable trend rather than a single instant.

SMS2 uses a fixed primary directory, versioned spill summaries, and bounded
overflow cells. It does not expose tombstone-pressure or synchronous-compaction
diagnostics.

## Runtime-local counters

The following values describe work performed through the current runtime or
handle. They are not expected to match another participant:

- `AbortedReservationCount`;
- `RecoveredLeaseCount`, `ActiveLeaseRecoveryCount`,
  `UnsupportedLeaseRecoveryCount`, and `FailedLeaseRecoveryCount`;
- `RecoveredReservationCount`, `ActiveReservationRecoveryCount`,
  `UnsupportedReservationRecoveryCount`, and
  `FailedReservationRecoveryCount`;
- `CapacityPressureCount`;
- recent/max probe and overflow-scan lengths;
- `OverflowScanCount`;
- `CasRetryCount` and `HelpedTransitionCount`;
- `ContentionBudgetExhaustionCount`;
- `InvalidTokenCount` and `StaleTokenCount`;
- `RecoveryAttemptCount` and `RecoveredTransitionCount`;
- current/live/stale/unsupported/inconsistent/changing owner-classification
  counters;
- `LastFailureStatus`; and
- `GetFailureCount(StoreStatus)` or the language-equivalent status counters.

Interop tooling must compare shared structural facts while checking only the
presence and meaning of local counters. Equal local counters are coincidental.

## Reading pressure

Useful capacity interpretations:

- low `FreeSlotCount` with many published values means configured value
  capacity is genuinely occupied;
- high `PendingRemovalCount` with active leases means readers delay physical
  reuse;
- high `ReservedSlotCount` means producers hold incomplete reservations;
- `LeaseTableFull` with a low slot count points to lease-record sizing rather
  than value capacity;
- `IsParticipantTableExhausted` points to open-handle capacity;
- spilled buckets and overflow occupancy indicate directory collision pressure;
  and
- retired records indicate generation/incarnation reuse protection, not
  transient contention.

`StoreFull`, `LeaseTableFull`, and `ParticipantTableFull` are capacity outcomes,
not corruption.

## Reading contention and helping

`CasRetryCount` measures failed compare/exchange attempts recorded locally.
`HelpedTransitionCount` measures cooperative completion of another
participant's published transition. `ContentionBudgetExhaustionCount` counts
local calls that returned `StoreBusy` after bounded retry/revalidation/helping.

These counters help distinguish sustained contention from a cold-open wait. Hot
operations never acquire the platform lifecycle lock, so a high hot-path retry
count should not be explained as mutex ownership.

## Recovery and owner classification

Recovery metrics should be interpreted together:

- an attempt is a record considered for explicit recovery;
- a recovered transition is one exact compare/exchange that reclaimed eligible
  state;
- current/live classifications are retained;
- stale classifications may be reclaimed after unchanged-state revalidation;
- unsupported or inconsistent classifications are retained conservatively; and
- changing classifications mean the record moved during the observation.

Never infer that unsupported owner evidence is stale. Linux PID namespace and
owner-anchor evidence, Windows process identity, permissions, and host support
all affect classification.

## Failure counts

`LastFailureStatus` is the latest non-success result recorded by this handle.
Use `GetFailureCount(status)` for a specific deterministic status. Successful
operations do not erase earlier counts.

Input errors, capacity, `StoreBusy`, cancellation, and lifecycle races must not
latch corruption. `CorruptStore` is reserved for a persistent impossible shared
state after revalidation.

## Diagnostics after close

The managed handle preserves its last structural snapshot for safe formatting
after disposal and continues to report local `StoreDisposed` outcomes. It does
not re-enter mapped memory. C++ and Python wrappers follow their public closed-
handle contracts and must not expose a borrowed mapped view after close.

## Operational collection

For useful evidence, record:

- timestamp and process/runtime identity;
- package version and five-field protocol identity;
- public store name only when it is not sensitive;
- configured capacities;
- shared slot/lease/participant/directory facts;
- local retry/help/token/recovery/status counters;
- host OS, architecture, container namespace, and permissions; and
- recent crashes, cancellations, recovery scans, and deployment replacement.

Avoid dumping key, descriptor, or payload bytes by default. SharedMemoryStore
does not know whether those bytes contain secrets.

## Related guides

- [Errors and statuses](errors.md)
- [Usage](usage.md)
- [Architecture](architecture.md)
- [Portability](portability.md)
