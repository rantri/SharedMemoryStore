# Performance Scope

SharedMemoryStore is designed to avoid per-operation managed heap allocation in
hot paths after initialization and warm-up. That contract is part of the
[public API contract](../specs/001-frame-memory-store/contracts/public-api.md).
Expected failure timing is described in the
[error taxonomy contract](../specs/001-frame-memory-store/contracts/error-taxonomy.md).

## What Is Measured

The repository includes benchmark projects under `benchmarks/` for lifecycle,
publish allocation, lease allocation, failure latency, frame throughput, remove
and reuse, and reuse stress scenarios.

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

Run benchmarks on an otherwise quiet machine and record OS, CPU, .NET SDK, and
package version with the result. If a performance claim is added to public docs,
include the measured scenario and update release notes when the claim changes.

## Capacity Pressure

Use [Diagnostics](diagnostics.md) to track `StoreFull`, `LeaseTableFull`, and
`CapacityPressureCount` signals. Capacity pressure is usually a configuration or
consumer-lifecycle issue: increase slot count, increase lease record count,
release leases sooner, or reduce removal pressure.
