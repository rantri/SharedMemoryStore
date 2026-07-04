# Performance Scope

SharedMemoryStore is designed to avoid per-operation managed heap allocation in
hot paths after initialization and warm-up. That expectation is tied to the
[public-api.md](../specs/001-frame-memory-store/contracts/public-api.md), status
outcomes are tied to
[error-taxonomy.md](../specs/001-frame-memory-store/contracts/error-taxonomy.md),
and key-index health is tied to
[index-health-contract.md](../specs/004-store-reliability-hardening/contracts/index-health-contract.md).

This page separates measured results, design expectations, benchmark method,
capacity assumptions, platform assumptions, and unvalidated scenarios.

## Measured Areas

The repository includes benchmarks under
[`benchmarks/SharedMemoryStore.Benchmarks/`](../benchmarks/SharedMemoryStore.Benchmarks/)
for:

- lifecycle open/create and disposal paths.
- publish allocation and throughput.
- lease allocation and acquire/release behavior.
- failure latency.
- frame throughput.
- direct ingest allocation and throughput.
- segmented publish.
- remove and reuse.
- reliability recovery and lifecycle stress.
- key-index tombstone pressure.

Benchmark results are environment-specific. Treat them as local validation data,
not hardware guarantees.

## Design Expectations

The current design expects:

- fixed-capacity shared-memory storage from `SharedMemoryStoreOptions`.
- immutable published payload bytes.
- direct reservation writes into store-owned memory after capacity is reserved.
- segmented publish to copy existing segments into one committed store value.
- status-returning pressure and contention outcomes.
- caller-owned diagnostics, retries, logging, and metrics.
- no hidden background workers in the core package.

## Running Benchmarks

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release
```

Focused ingest benchmarks:

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --filter *DirectIngest*
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --filter *SegmentedPublish*
```

Sustained throughput validation:

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --validation sustained-throughput
```

Direct allocation validation:

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --validation direct-allocation
```

Record OS, CPU, .NET SDK, package version, payload bytes, descriptor bytes,
slot count, lease-record count, producer count, reader count, segment count,
copied bytes, final status, and benchmark command with any public performance
claim.

## Capacity Assumptions

Capacity is fixed at create/open time. `SlotCount` must cover published values,
pending removals, and pending reservations. `LeaseRecordCount` must cover
concurrent active readers. `MaxKeyBytes`, `MaxDescriptorBytes`, and
`MaxValueBytes` must cover encoded keys, descriptors, and payloads.

Use [Diagnostics](diagnostics.md) to track `StoreFull`, `LeaseTableFull`,
`CapacityPressureCount`, active leases, active reservations, pending removals,
and key-index health.

## Tombstone Pressure

The key index uses open addressing and tombstones to preserve probe chains after
removal. High unique-key churn can make missing-key lookup and new-key insert
paths probe longer even when live slot capacity is available. Diagnostic
snapshots expose occupied, tombstone, empty, usable capacity, probe length, and
compaction counters.

The current internal threshold compacts synchronously under the store mutation
lock when tombstones reach 35% of index entries, when no empty probe terminators
remain, or when observed probe length reaches 75% of index capacity. This is a
current implementation detail for maintainers, not a public maintenance API.

## Platform Assumptions

Current validation targets `.NET 10` on Linux, Windows, and the supported
same-host Docker profile. Unsupported platforms or restricted environments may
return documented unsupported or environment failure outcomes. See
[Portability](portability.md) before publishing platform claims.

## Not Claimed

The documentation does not promise:

- a fixed throughput number on unmeasured hardware.
- a latency percentile for every OS, CPU, storage, virtualization, or container
  setup.
- cross-host cache behavior.
- persistence after process and mapping lifetime.
- application-specific frame parsing performance.
- protection from malicious writers that already have mapping access.
- current C++ or Python bindings.
- stable benchmark results across future API or layout changes.

## Release Review

If a performance claim is added, changed, or removed, update:

- this page.
- [Release preparation](releases.md).
- [Maintainers](maintainers.md).
- [CHANGELOG.md](../CHANGELOG.md) when release-facing behavior or support
  claims change.
- benchmark result notes or validation evidence.
