# Contract: Diagnostics and Optional Integration

## Diagnostics Contract

`DiagnosticsSnapshot` remains a caller-requested snapshot. Reading diagnostics
does not write to console, start background work, or change store contents.

Stable failure access:

```csharp
public readonly struct DiagnosticsSnapshot
{
    public StoreStatus LastFailureStatus { get; }
    public long GetFailureCount(StoreStatus status);
}
```

Capacity, lifecycle, recovery, and index-health summary properties may remain
as named properties because they describe store state rather than duplicating a
status taxonomy.

## Convenience Failure Count Pruning

Per-status convenience properties that duplicate `GetFailureCount` are removed
or obsoleted before the production API release when their names are brittle,
misleading, or clunky. Migration notes must map each removed property to:

```csharp
snapshot.GetFailureCount(StoreStatus.SomeStatus)
```

Examples of names to prune include:
- `UnknownFailureFailures`.
- Duplicated aliases such as `FailedCommitCount` when the same information is
  available through `ReservationIncomplete` failure counts.
- Any new convenience property that would need to be added every time
  `StoreStatus` changes.

## Optional Integration Contract

The core `SharedMemoryStore` package must remain usable without hosting,
dependency-injection, logging, health-check, or options-framework dependencies.

If service-hosting integration is delivered by this feature, it must be:
- A separate package or sample, such as `SharedMemoryStore.Hosting`.
- Opt-in for consumers.
- Tested independently from the core package.
- Focused on lifecycle validation, health reporting, graceful shutdown, and
  cleanup or recovery hooks.

## Interface Rules

Do not add a broad interface that mirrors the concrete store API. Consumer-facing
interfaces are allowed only when they represent narrow boundaries, such as:
- Health probing.
- Lifecycle start/stop and cleanup.
- Read-only lookup behavior.
- Write-only publish behavior.

Low-level lease, reservation, span, memory, and shared-layout details must not
be forced into a broad application-facing interface.

## Tests

Validation must prove:
- Core package restore, pack, and package consumption do not include optional
  hosting dependencies.
- Diagnostics failure counts are available by aggregate status.
- Removed or obsolete diagnostics convenience members have release-note
  migration guidance.
- Any optional integration package exposes only focused lifecycle and health
  contracts.
- No sample or test requires a broad `ISharedMemoryStore`-style interface.
