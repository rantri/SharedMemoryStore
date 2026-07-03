# Quickstart: Validate API Production Readiness

This guide validates the planned production API surface from a clean checkout.
It is a validation guide, not an implementation recipe.

## Prerequisites

- .NET 10 SDK installed.
- Repository checked out at `SharedMemoryStore`.
- Feature artifacts under `specs/005-api-production-readiness/`.

## 1. Restore and Build

```powershell
dotnet restore .\SharedMemoryStore.slnx
dotnet build .\SharedMemoryStore.slnx -c Release --no-restore
```

Expected outcome:
- Core package builds with no runtime dependency added beyond the BCL.
- Optional integration projects, if present, build separately and do not change
  core package dependencies.

## 2. Validate Public Store Identity

Run contract and package-consumption tests that compile examples against the
final API:

```powershell
dotnet test .\tests\SharedMemoryStore.ContractTests\SharedMemoryStore.ContractTests.csproj -c Release --filter ProductionApiContract
.\scripts\validate-package-consumption.ps1
.\scripts\validate-docs.ps1
```

Expected outcome:
- Examples import `SharedMemoryStore` and reference `MemoryStore` without
  namespace/type aliases.
- Release notes document migration from the pre-release primary type.

## 3. Validate Reservation Memory Lifetime

Run reservation lifetime tests:

```powershell
dotnet test .\tests\SharedMemoryStore.UnitTests\SharedMemoryStore.UnitTests.csproj -c Release --filter ReservationMemoryLifetime
dotnet test .\tests\SharedMemoryStore.IntegrationTests\SharedMemoryStore.IntegrationTests.csproj -c Release --filter ReservationReuseSafety
```

Expected outcome:
- Retained safe public write access cannot mutate committed payloads.
- Aborted, disposed, recovered, and store-disposed reservations cannot mutate
  future values after slot reuse.
- The public quickstart does not use retained writable `Memory<byte>`.

## 4. Validate Bounded Wait Behavior

Run contention tests:

```powershell
dotnet test .\tests\SharedMemoryStore.UnitTests\SharedMemoryStore.UnitTests.csproj -c Release --filter StoreWaitPolicy
dotnet test .\tests\SharedMemoryStore.IntegrationTests\SharedMemoryStore.IntegrationTests.csproj -c Release --filter ContendedSynchronization
```

Expected outcome:
- Every public operation that can wait on shared synchronization returns the
  documented busy, timeout, cancellation, or disposed outcome.
- No operation mutates shared state when synchronization is not acquired.
- Default wait behavior is one second and tests allow 250 milliseconds of
  scheduler tolerance.
- Diagnostics expose a status-returning wait-aware path.

## 5. Validate Configuration and Status Taxonomy

Run option, key, status, and diagnostics tests:

```powershell
dotnet test .\tests\SharedMemoryStore.UnitTests\SharedMemoryStore.UnitTests.csproj -c Release --filter "StoreOptionsValidation|KeyValidation|DiagnosticsApiShape"
dotnet test .\tests\SharedMemoryStore.ContractTests\SharedMemoryStore.ContractTests.csproj -c Release --filter "ConfigurationContract|DiagnosticsContract|ContentionContract"
```

Expected outcome:
- Undefined `OpenMode` values are rejected as invalid options.
- Size helpers derive required bytes for ordinary configurations.
- Empty keys return `InvalidKey`; oversized keys return `KeyTooLarge`.
- Diagnostics failure counts are accessible through
  `GetFailureCount(StoreStatus)`.

## 6. Validate Optional Integration Boundary

If `SharedMemoryStore.Hosting` or an equivalent optional adapter is implemented,
run its tests separately:

```powershell
dotnet test .\tests\SharedMemoryStore.Hosting.Tests\SharedMemoryStore.Hosting.Tests.csproj -c Release
```

Expected outcome:
- Hosted lifecycle and health behavior are opt-in.
- The core package can still be restored, packed, and consumed without hosting
  dependencies.
- No broad concrete-store mirror interface is introduced.
- If the hosted sample is present, it builds and runs its lifecycle, health,
  shutdown, cleanup, and recovery validation.

## 7. Run Release Validation

```powershell
dotnet test .\SharedMemoryStore.slnx -c Release
dotnet pack .\src\SharedMemoryStore\SharedMemoryStore.csproj -c Release --no-build
```

Expected outcome:
- Full test suite passes.
- Package builds with XML documentation and release notes.
- Public docs, samples, contracts, and package metadata describe the production
  API readiness changes.
