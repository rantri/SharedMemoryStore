# Performance Scope

SharedMemoryStore is designed to avoid per-operation managed heap allocation in
hot paths after initialization and warm-up. That contract is part of the
[public API contract](../specs/001-frame-memory-store/contracts/public-api.md).
Expected failure timing is described in the
[error taxonomy contract](../specs/001-frame-memory-store/contracts/error-taxonomy.md).

## What Is Measured

The repository includes benchmark projects under `benchmarks/` for lifecycle,
publish allocation, lease allocation, failure latency, frame throughput, direct
ingest allocation, direct ingest throughput, segmented publish, remove and reuse,
and reuse stress scenarios.

Benchmark results are environment-specific. Treat them as local validation data,
not hardware guarantees.

## What Is Not Guaranteed

The documentation does not promise:

- a fixed throughput number on unmeasured hardware.
- a latency percentile for every OS, CPU, storage, or virtualization setup.
- a network-distributed cache behavior.
- application-specific frame parsing performance.
- stable benchmark results across prerelease API or layout changes.

## Running Benchmarks

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release
```

Focused ingest benchmarks:

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --filter *DirectIngest*
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --filter *SegmentedPublish*
```

Sustained throughput validation runs the direct-ingest and simple-publish
60-second loops once each and prints the relative comparison for release notes:

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --validation sustained-throughput
```

The 100,000-frame allocation validation can also be run once without repeated
BenchmarkDotNet measurement iterations:

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --validation direct-allocation
```

Run benchmarks on an otherwise quiet machine and record OS, CPU, .NET SDK, and
package version with the result. If a performance claim is added to public docs,
include the measured scenario and update release notes when the claim changes.

For ingest validation, record payload bytes, descriptor bytes, slot count,
producer count, reader count, segment count, copied bytes, final commit or
recovery status, and the benchmark environment summary.

The `DirectIngestAllocationBenchmarks.ValidateOneHundredThousandFramesAllocation`
result records `FrameCount`, `TotalAllocatedBytes`, `AllocatedBytesPerFrame`,
`FinalStatus`, and `Passed`. For SC-001 readiness, `FrameCount` must equal
`BenchmarkEnvironment.DirectIngestAllocationFrames`, `FinalStatus` must be
`Success`, and `TotalAllocatedBytes` must be `0`.

The direct ingest throughput benchmark models the socket-style path by writing
directly into reservation memory after capacity is reserved. It does not stage a
producer-owned full-payload array before publication.

## Capacity Pressure

Use [Diagnostics](diagnostics.md) to track `StoreFull`, `LeaseTableFull`, and
`CapacityPressureCount` signals. Capacity pressure is usually a configuration or
consumer-lifecycle issue: increase slot count, increase lease record count,
release leases sooner, or reduce removal pressure.

## Tombstone Pressure

The key index uses open addressing and tombstones to preserve probe chains after
removal. High unique-key churn can make missing-key lookup and new-key insert
paths probe longer even when live slot capacity is available. Diagnostic
snapshots expose occupied, tombstone, empty, usable capacity, probe length, and
compaction counters so consumers can distinguish churn pressure from live
capacity pressure.

The current internal threshold compacts synchronously under the store mutation
lock when tombstones reach 35% of index entries, when no empty probe terminators
remain, or when observed probe length reaches 75% of index capacity. The
`TombstonePressureBenchmarks` benchmark records clean-index missing-lookup and
new-insert baselines, managed-pressure timings, early pressure detection before
the 75% worst-case probe threshold, and preservation checks for active leases,
pending reservations, duplicate detection, and visible values without adding a
public maintenance API or background worker.
