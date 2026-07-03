# Quickstart Validation Guide

This guide describes validation scenarios for the reliability hardening plan.
It is not an implementation script.

## Prerequisites

- .NET SDK compatible with `net10.0`.
- Windows x64 for full multi-process owner-liveness validation.
- Clean checkout with the source layout described in [plan.md](plan.md).

## Build

```powershell
dotnet restore
dotnet build -c Release
```

Expected outcome:
- runtime package builds for `net10.0`.
- runtime package dependencies remain limited to the .NET BCL.
- XML documentation is generated for corrected recovery, lifecycle, rollover,
  and diagnostics behavior.

## Owner Recovery Unit and Contract Tests

```powershell
dotnet test tests/SharedMemoryStore.UnitTests/SharedMemoryStore.UnitTests.csproj -c Release --filter "FullyQualifiedName~LeaseRecoveryOwnership"
dotnet test tests/SharedMemoryStore.ContractTests/SharedMemoryStore.ContractTests.csproj -c Release --filter "FullyQualifiedName~ReliabilityApiContract"
```

Expected outcome:
- current-process recovery recovers only eligible current-process or stale-owner
  leases.
- other live-owner leases remain valid and continue protecting storage.
- disabled or unsupported recovery mutates no active lease.
- reports and diagnostics distinguish recovered, active, unsupported, and failed
  recovery decisions according to
  [owner-recovery-contract.md](contracts/owner-recovery-contract.md).

## Multi-Owner Recovery Integration

```powershell
dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~MultiOwnerLeaseRecovery"
```

Expected outcome:
- two process owners can hold leases against the same named store.
- one owner running current-process recovery cannot reclaim another live
  owner's lease.
- stale owner records are recovered or reported unsupported or unsafe without
  changing visible value contents.
- slot reuse happens only after every protecting lease is released or
  legitimately recovered.

## Disposal Race Stress

```powershell
dotnet test tests/SharedMemoryStore.UnitTests/SharedMemoryStore.UnitTests.csproj -c Release --filter "FullyQualifiedName~StoreDisposalRace"
dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~StoreDisposalRace"
```

Expected outcome:
- at least 100,000 operations race publish, reserve, acquire, remove, recovery,
  diagnostics, release, reservation advance, commit, abort, token dispose, and
  store dispose.
- every operation either completes before disposal or returns the documented
  disposed, invalid, empty, or already-completed outcome from
  [disposal-rollover-contract.md](contracts/disposal-rollover-contract.md).
- no public operation exposes internal disposed-resource exceptions.
- repeated concurrent dispose calls are idempotent.

## Rollover Boundary Validation

```powershell
dotnet test tests/SharedMemoryStore.UnitTests/SharedMemoryStore.UnitTests.csproj -c Release --filter "FullyQualifiedName~ProbeRollover"
dotnet test tests/SharedMemoryStore.UnitTests/SharedMemoryStore.UnitTests.csproj -c Release --filter "FullyQualifiedName~SlotLifecycleIdentifier"
dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~RolloverStress"
```

Expected outcome:
- slot and lease probe cursors seeded near rollover produce only valid indexes.
- capacity-one slot and lease tables return deterministic outcomes.
- slot lifecycle identity advances across generation boundaries without
  accepting stale leases or reservations.
- boundary tests complete at least 1,000,000 additional operations without
  invalid indexes, runtime overflow failures, or stale handle acceptance.

## Tombstone Diagnostics and Churn

```powershell
dotnet test tests/SharedMemoryStore.UnitTests/SharedMemoryStore.UnitTests.csproj -c Release --filter "FullyQualifiedName~IndexHealth"
dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~TombstonePressure"
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks -c Release -- --validation tombstone-pressure
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks -c Release -- --filter *TombstonePressure*
```

Expected outcome:
- diagnostic snapshots distinguish occupied, tombstone, empty, and usable index
  capacity according to
  [index-health-contract.md](contracts/index-health-contract.md).
- high-churn unique-key workloads expose pressure before 75% of measured
  worst-case probe cost.
- after pressure management, missing-key lookup and new-key insert latency stay
  within 2x of a clean-index baseline at the same configured capacity.
- visible values, duplicate-key detection, active leases, pending reservations,
  and slot reuse remain correct.

## Full Regression and Package Validation

```powershell
dotnet test -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
powershell -ExecutionPolicy Bypass -File scripts/validate-package-consumption.ps1
powershell -ExecutionPolicy Bypass -File scripts/validate-docs.ps1
```

Expected outcome:
- existing publish, reserve, acquire, remove, release, recovery, diagnostics,
  package, and documentation validation continues to pass.
- package consumers can understand corrected recovery policy and disposal
  outcomes from docs and XML documentation without reading implementation
  internals.
- release notes describe semantic version impact and compatibility behavior.
