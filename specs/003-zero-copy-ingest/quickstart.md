# Quickstart Validation Guide

This guide describes the validation scenarios for the planned implementation.
It is not an implementation script.

## Prerequisites

- .NET SDK compatible with `net10.0`.
- Windows x64 for the first validated direct-ingest benchmark target.
- Clean checkout with the source layout described in [plan.md](plan.md).

## Build

```powershell
dotnet restore
dotnet build -c Release
```

Expected outcome:
- runtime package builds for `net10.0`.
- XML documentation is produced for reservation APIs.
- runtime package dependencies remain limited to the .NET BCL.

## Unit and Contract Tests

```powershell
dotnet test tests/SharedMemoryStore.UnitTests/SharedMemoryStore.UnitTests.csproj -c Release
dotnet test tests/SharedMemoryStore.ContractTests/SharedMemoryStore.ContractTests.csproj -c Release
```

Expected outcome:
- reservation API members and appended `StoreStatus` values match
  [reservation-api.md](contracts/reservation-api.md) and
  [diagnostics-and-errors.md](contracts/diagnostics-and-errors.md).
- layout constants, minor version, and slot metadata meanings match
  [ingest-layout.md](contracts/ingest-layout.md).
- duplicate pending keys are rejected.
- pending reservations are invisible to acquire.
- commit before exact payload completion returns `ReservationIncomplete`.
- advance beyond the reserved length returns `ReservationWriteOutOfRange`.
- commit after abort and abort after commit return deterministic statuses.
- simple `TryPublish` contract tests continue to pass unchanged.

## Direct Reservation Integration

```powershell
dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~ZeroCopyIngest"
```

Expected outcome:
- producer reserves by key, payload length, and descriptor.
- producer fills store-owned memory through `GetSpan` or `GetMemory`.
- `Advance` tracks exact bytes written.
- commit publishes one immutable value visible by key.
- readers observe descriptor and payload bytes matching the written frame.
- no reader observes partial bytes before commit.

## Segmented Publish Integration

```powershell
dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SegmentedFrame"
```

Expected outcome:
- a frame split across at least 16 segments is stored as one committed value.
- stored bytes match the logical concatenation of all segments.
- no temporary contiguous full-frame array is allocated by the library.
- one-segment and many-segment paths share the same public workflow.

## Abort and Recovery Integration

```powershell
dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~ReservationRecovery"
```

Expected outcome:
- abort before commit leaves the key unavailable and returns the slot to the
  free pool.
- disposing an active reservation aborts it.
- explicit recovery reclaims stale pending reservations according to owner
  policy.
- recovered reservations never become visible to readers.
- recovery reports scanned, recovered, active, unsupported, and failed counts.

## Allocation Validation

```powershell
dotnet test tests/SharedMemoryStore.UnitTests/SharedMemoryStore.UnitTests.csproj -c Release --filter "FullyQualifiedName~ReservationAllocation"
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks -c Release -- --filter *DirectIngestAllocation*
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks -c Release -- --filter *SegmentedPublish*
```

Expected outcome:
- after initialization and warm-up, direct reserve/fill/advance/commit/remove
  reports 0 managed heap bytes allocated per frame for payload storage.
- segmented publish reports no temporary full-payload allocation.
- benchmark output records SDK, OS, CPU, slot count, payload size, descriptor
  size, producer count, reader count, and commit/recovery status.

## Visibility and Concurrency Stress

```powershell
dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~IngestVisibilityConcurrency"
```

Expected outcome:
- readers never acquire pending, aborted, failed, or stale reservations.
- readers acquire committed reservation values through unchanged `ValueLease`
  semantics.
- remove while readers hold leases protects storage until final release.
- existing simple publish, acquire, release, remove, and reuse tests continue to
  pass in the same run.

## Throughput Benchmark

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks -c Release -- --filter *DirectIngestFrameThroughput*
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks -c Release -- --filter *FrameThroughput*
```

Expected outcome:
- direct ingest benchmark uses the documented 1.3 MB frame-shaped workload.
- direct ingest sustains at least the same frame rate as existing simple publish
  for the benchmark environment.
- release notes record relative throughput improvement or regression.

## Sample and Package Consumption

```powershell
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
powershell -ExecutionPolicy Bypass -File scripts/validate-package-consumption.ps1
```

Expected outcome:
- sample demonstrates length-prefixed frame reservation, exact receive progress,
  commit, reader acquire, removal, lease release, abort cleanup, and segmented
  publish.
- a clean consumer project can use only public package APIs and docs to run the
  ingest workflow in under 10 minutes.
- package README, XML documentation, changelog, examples, lifecycle,
  diagnostics, performance, and portability docs describe the new contract.
