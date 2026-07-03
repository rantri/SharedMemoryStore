# Index Health Contract

## Package Impact

- Package id remains `SharedMemoryStore`.
- Target framework remains `net10.0`.
- Runtime dependencies remain .NET BCL only.
- Semantic version impact: minor pre-1.0 package update if
  `DiagnosticsSnapshot` adds index-health members; patch-level reliability fix
  if the same diagnostics can be exposed without public shape changes.
- No public maintenance API is planned unless benchmark evidence shows
  diagnostics plus internal management cannot meet the success criteria.

## Diagnostic Snapshot Additions

`DiagnosticsSnapshot` must expose enough information for consumers to identify
tombstone pressure separately from live-entry capacity pressure.

Required concepts:

```csharp
public int IndexEntryCount { get; }
public int OccupiedIndexEntryCount { get; }
public int TombstoneIndexEntryCount { get; }
public int EmptyIndexEntryCount { get; }
public double TombstonePressureRatio { get; }
public int UsableIndexCapacity { get; }
public int LastObservedProbeLength { get; }
public int MaxObservedProbeLength { get; }
public long IndexCompactionCount { get; }
```

The final implementation may choose integer-only pressure fields if that better
fits the allocation contract, but consumers must be able to calculate
tombstone pressure and distinguish it from occupied-entry pressure.

## Probe Measurement Rules

- `TryFind`, `TryInsert`, and remove paths should record bounded probe counts
  without heap allocation.
- Probe counts are diagnostic signals and must not change success or failure
  semantics by themselves.
- Diagnostics must not expose key bytes, descriptor bytes, or payload bytes.
- Diagnostics must remain caller-controlled through `GetDiagnostics()`.
- Disposed diagnostics must not access disposed mapped memory.

## Tombstone Pressure Rules

The key index uses open addressing with `Empty`, `Occupied`, and `Tombstone`
states. Tombstones preserve probe chains after removal but can degrade
missing-key lookup and insert cost.

Required behavior:
- missing-key lookups remain bounded by index entry count.
- inserts may reuse tombstones.
- diagnostics identify live occupied entries, tombstones, empty entries, and
  usable capacity.
- pressure threshold is selected from churn benchmark evidence.
- pressure is detected before the benchmark reaches 75% of measured worst-case
  probe cost.

## Internal Maintenance Rules

If benchmark evidence shows diagnostics alone are insufficient, the
implementation must use bounded synchronous internal maintenance:

- run under the existing store lock or equivalent exclusive mutation boundary.
- rebuild or compact occupied entries while clearing tombstones.
- preserve duplicate-key detection.
- preserve pending reservations and pending removals.
- preserve active reader lease protection and slot reuse behavior.
- avoid background threads, timers, global mutable configuration, and direct
  console output.
- return deterministic failures if compaction cannot complete safely.

Compaction must never make an uncommitted reservation visible, lose a published
key, resurrect a removed key, or change payload or descriptor bytes.

## Public Maintenance API Rule

A public maintenance operation such as `TryCompactIndex()` must not be added
unless the churn benchmark demonstrates that diagnostics plus bounded internal
management cannot meet:

- missing-key lookup and new-key insert latency within 2x of a clean-index
  baseline at the same capacity.
- pressure detection before 75% of measured worst-case probe cost.
- preservation of existing package behavior.

If such an API is later justified, it requires a separate contract section,
XML documentation, compatibility notes, and package release notes.

## Contract Tests and Benchmarks

Required coverage:
- diagnostic snapshots distinguish occupied, tombstone, and empty entries.
- a removed key can be inserted again after pressure management.
- missing-key lookups against high tombstone pressure do not sustain near
  full-table scans after management.
- duplicate-key detection remains correct during and after compaction.
- committed values and active leases remain valid during and after compaction.
- pending reservations remain invisible and duplicate-blocking.
- high-churn benchmark records clean baseline, pressure state, management
  behavior, and post-management latency.
