# Quickstart Validation Guide

This guide describes the validation scenarios for the planned implementation.
It is not an implementation script.

## Prerequisites

- .NET SDK 10.0.201 or newer compatible .NET 10 SDK.
- Windows x64 for the first supported benchmark target.
- Clean checkout with the planned source layout from [plan.md](plan.md).

## Build

```powershell
dotnet restore
dotnet build -c Release
```

Expected outcome:
- runtime package builds for `net10.0`.
- XML documentation is produced for public APIs.
- no runtime package dependency is added beyond the .NET BCL.

## Unit and Contract Tests

```powershell
dotnet test -c Release --filter "Category!=Integration&Category!=Benchmark"
```

Expected outcome:
- option validation is deterministic.
- key, descriptor, and value size boundaries are covered.
- duplicate, missing, full, oversized, invalid release, disposed, and corrupted
  store statuses match [error-taxonomy.md](contracts/error-taxonomy.md).
- public API and shared layout constants match
  [public-api.md](contracts/public-api.md) and
  [shared-memory-layout.md](contracts/shared-memory-layout.md).

## Integration Tests

```powershell
dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release
```

Expected outcome:
- create/open of a named store succeeds.
- publishing a value makes it immediately acquirable by key.
- multiple readers observe identical bytes.
- remove while leased leaves storage protected.
- final release of a removed value returns its slot to the free pool.
- explicit stale-lease recovery behavior is deterministic for the platform.

## Allocation Validation

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks -c Release -- --filter *Allocation*
```

Expected outcome:
- after setup and warm-up, publish, acquire, release, remove, and reuse report
  0 managed heap bytes allocated per operation.
- benchmark output records SDK, OS, CPU, slot configuration, value size,
  descriptor size, and reader count.

## Frame-Shaped Value Scenario

```powershell
dotnet run --project samples/FrameValue -c Release
```

Expected outcome:
- a sample creates a descriptor that explains a frame-shaped payload containing
  header, metadata, and binary data.
- the store treats the frame as opaque bytes.
- multiple readers acquire the value and interpret the descriptor outside the
  core store.
- release, remove, and reuse use the same APIs as non-frame values.

## Package Consumption Scenario

```powershell
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
dotnet new console -f net10.0 -n SharedMemoryStore.ConsumerSmoke -o artifacts/consumer-smoke
dotnet add artifacts/consumer-smoke/SharedMemoryStore.ConsumerSmoke.csproj package SharedMemoryStore --source artifacts/package
dotnet run --project artifacts/consumer-smoke/SharedMemoryStore.ConsumerSmoke.csproj -c Release
```

Expected outcome:
- a clean consumer project installs the package from local artifacts.
- the consumer uses only public APIs to create/open a store, publish, acquire,
  release, remove, and observe slot reuse.
- the scenario completes in under 5 minutes using public documentation.

## Throughput and Reuse Benchmarks

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks -c Release -- --filter *FrameThroughput*
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks -c Release -- --filter *Reuse*
```

Expected outcome:
- at least 500 publishes per second for 1.3 MB values for 60 seconds on
  documented local benchmark hardware.
- 100,000 publish/acquire/release/remove cycles complete with one producer and
  four readers without usage count underflow, leaked active leases, or
  use-after-release detection failures.
- after one million publish/remove/reuse cycles, committed memory remains within
  1% of configured capacity plus documented fixed overhead.
