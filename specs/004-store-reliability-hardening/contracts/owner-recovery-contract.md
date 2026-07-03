# Owner Recovery Contract

## Package Impact

- Package id remains `SharedMemoryStore`.
- Target framework remains `net10.0`.
- Runtime dependencies remain .NET BCL only.
- Semantic version impact: patch-level reliability fix when the existing public
  report shape can be preserved; minor pre-1.0 package update if additive
  recovery report or diagnostic members are required.
- Existing publish, reserve, acquire, remove, release, reservation recovery, and
  diagnostics semantics remain compatible except for corrected unsafe lease
  recovery outcomes.

## Public Surface

Required member on `SharedMemoryStore` remains:

```csharp
StoreStatus TryRecoverLeases(
    in LeaseRecoveryOptions options,
    out LeaseRecoveryReport report);
```

Required option remains:

```csharp
public readonly record struct LeaseRecoveryOptions(
    bool RecoverCurrentProcessLeases);
```

`LeaseRecoveryReport` must expose the following consumer-visible decisions. The
implementation may preserve the existing three-field constructor and add
properties or a compatible constructor, but these concepts must be available to
callers and covered by contract tests:

```csharp
public readonly record struct LeaseRecoveryReport
{
    public int ScannedRecordCount { get; }
    public int RecoveredLeaseCount { get; }
    public int ActiveLeaseCount { get; }
    public int UnsupportedLeaseCount { get; }
    public int FailedRecoveryCount { get; }
}
```

## Owner Categories

Lease recovery must classify every active record before mutating it:

- `CurrentProcess`: `OwnerProcessId == Environment.ProcessId`.
- `OtherLiveProcess`: owner process can be verified alive and is not the current
  process.
- `StaleProcess`: owner process can be verified absent, exited, or invalid.
- `Unsupported`: platform or runtime cannot safely evaluate owner liveness.
- `UnsafeRecord`: slot index, lifecycle identity, state, or usage count is
  inconsistent with a safe recovery mutation.

## Recovery Policy

When `RecoverCurrentProcessLeases` is `false`:
- recover only `StaleProcess` records.
- skip `CurrentProcess` records as active.
- skip `OtherLiveProcess` records as active.
- count unsupported liveness checks as unsupported.
- count inconsistent state as failed recovery.

When `RecoverCurrentProcessLeases` is `true`:
- recover `CurrentProcess` records.
- recover `StaleProcess` records.
- skip `OtherLiveProcess` records as active.
- count unsupported liveness checks as unsupported.
- count inconsistent state as failed recovery.

When lease recovery is disabled for the store:
- return `StoreStatus.UnsupportedPlatform` or the documented disabled-recovery
  status.
- do not mutate active lease records.
- return a report that makes no recovered leases visible.

## Mutation Rules

- A record may be changed from active to abandoned only after owner policy
  allows recovery and slot lifecycle identity validates.
- Slot usage count is decremented exactly once for each recovered active lease.
- If the recovered lease was the final usage on a pending-removal slot, normal
  final-release reclaim rules apply.
- Recovery must never decrement usage for another live owner skipped by policy.
- Recovery must never expose descriptor or payload bytes.
- Recovery must not accept stale lease tokens after slot reuse.

## Status and Diagnostics Rules

- `Success`: scan completed and every active record was recovered, skipped, or
  reported deterministically.
- `UnsupportedPlatform`: recovery is disabled or the platform cannot support
  the requested recovery operation at the store level.
- `CorruptStore`: shared state is impossible and unsafe to continue.
- `StoreDisposed`: the store was disposed before or during recovery.
- `UnknownFailure`: only for unexpected failures that cannot be mapped to a
  documented status.

Diagnostics must include lease recovery results through caller-controlled
snapshots or the report. The library must not write recovery decisions to
console, trace, logs, or metrics directly.

## Contract Tests

Required coverage:
- current-process recovery recovers current-process leases.
- current-process recovery skips other live process leases.
- stale-owner recovery recovers or reports stale leases without touching live
  owners.
- disabled recovery mutates no active leases.
- unsupported owner-liveness checks mutate no active leases.
- lifecycle identity mismatch is reported as failed or corrupt without slot
  reuse.
- recovery report categories match the scanned records.
- release after recovery returns deterministic released, invalid, or already
  completed outcomes.
