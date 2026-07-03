# Public Reference Map

This map ties public names, statuses, package metadata, and contract references
to the documentation surfaces that mention them.

## Package Identity

| Reference | Source | Public docs that must align |
|-----------|--------|-----------------------------|
| Package ID `SharedMemoryStore` | `src/SharedMemoryStore/SharedMemoryStore.csproj` | `README.md`, `docs/getting-started.md`, `docs/packaging.md`, `docs/releases.md`, `CHANGELOG.md` |
| Version `1.0.0` | `src/SharedMemoryStore/SharedMemoryStore.csproj` | `README.md`, `CHANGELOG.md`, `docs/packaging.md`, `docs/releases.md` |
| Target framework `net10.0` | `src/SharedMemoryStore/SharedMemoryStore.csproj` | `README.md`, `docs/getting-started.md`, `docs/portability.md`, sample READMEs |
| Runtime dependencies: .NET BCL only | `src/SharedMemoryStore/SharedMemoryStore.csproj`, constitution | `README.md`, `docs/integration.md`, `docs/packaging.md`, `docs/architecture.md` |
| Package README file `README.md` | `src/SharedMemoryStore/SharedMemoryStore.csproj` | `README.md`, `docs/packaging.md`, `docs/releases.md` |

## Public Types

| Type | Source | Documentation coverage |
|------|--------|------------------------|
| `MemoryStore` | `src/SharedMemoryStore/MemoryStore.cs` | `README.md`, `docs/getting-started.md`, `docs/usage.md`, `docs/examples.md`, `docs/lifecycle.md`, samples |
| `SharedMemoryStoreOptions` | `src/SharedMemoryStore/SharedMemoryStoreOptions.cs` | `docs/getting-started.md`, `docs/usage.md`, `docs/integration.md`, `docs/packaging.md` |
| `OpenMode` | `src/SharedMemoryStore/SharedMemoryStoreOptions.cs` | `docs/usage.md`, `docs/errors.md` |
| `StoreWaitOptions` | `src/SharedMemoryStore/StoreWaitOptions.cs` | `docs/usage.md`, `docs/errors.md`, `docs/integration.md` |
| `StoreOpenStatus` | `src/SharedMemoryStore/StoreStatus.cs` | `docs/getting-started.md`, `docs/errors.md`, `docs/usage.md` |
| `StoreStatus` | `src/SharedMemoryStore/StoreStatus.cs` | `docs/errors.md`, `docs/usage.md`, sample READMEs |
| `ReadOnlySpan<byte>` key, descriptor, and payload inputs | `src/SharedMemoryStore/MemoryStore.cs` | `docs/concepts.md`, `docs/byte-encoding.md`, `docs/usage.md`, `docs/examples.md`, `samples/BasicUsage/README.md` |
| `ValueLease` | `src/SharedMemoryStore/ValueLease.cs` | `docs/concepts.md`, `docs/usage.md`, `docs/lifecycle.md`, samples |
| `ValueReservation` | `src/SharedMemoryStore/Ingest/ValueReservation.cs` | `docs/concepts.md`, `docs/usage.md`, `docs/examples.md`, `docs/lifecycle.md`, `samples/ZeroCopyIngest/README.md` |
| `DiagnosticsSnapshot` | `src/SharedMemoryStore/Diagnostics/DiagnosticsSnapshot.cs` | `docs/diagnostics.md`, `docs/integration.md`, `docs/maintainers.md` |
| `LeaseRecoveryOptions` and `LeaseRecoveryReport` | `src/SharedMemoryStore/SharedMemoryStoreOptions.cs` | `docs/lifecycle.md`, `docs/diagnostics.md` |
| `ReservationRecoveryOptions` and `ReservationRecoveryReport` | `src/SharedMemoryStore/Ingest/ReservationRecovery.cs` | `docs/lifecycle.md`, `docs/diagnostics.md`, `samples/HostedServiceIntegration/README.md` |

## Public Methods and Members

| Member | Source | Documentation coverage |
|--------|--------|------------------------|
| `MemoryStore.TryCreateOrOpen` | `MemoryStore.cs` | First use, usage, integration |
| `MemoryStore.TryPublish` | `MemoryStore.cs` | First use, usage, examples, BasicUsage |
| `MemoryStore.TryAcquire` | `MemoryStore.cs` | Usage, lifecycle, examples, samples |
| `MemoryStore.TryRemove` | `MemoryStore.cs` | Usage, lifecycle, errors, samples |
| `MemoryStore.TryReserve` | `MemoryStore.cs` | Usage, examples, ZeroCopyIngest |
| `MemoryStore.TryPublishSegments` | `MemoryStore.cs` | Usage, examples, ZeroCopyIngest |
| `MemoryStore.TryRecoverLeases` | `MemoryStore.cs` | Lifecycle, diagnostics, HostedServiceIntegration |
| `MemoryStore.TryRecoverReservations` | `MemoryStore.cs` | Lifecycle, diagnostics, HostedServiceIntegration |
| `MemoryStore.GetDiagnostics` | `MemoryStore.cs` | Diagnostics, usage, BasicUsage |
| `MemoryStore.TryGetDiagnostics` | `MemoryStore.cs` | Diagnostics, integration, HostedServiceIntegration |
| `SharedMemoryStoreOptions.CalculateRequiredBytes` | `SharedMemoryStoreOptions.cs` | README, getting started, usage |
| `SharedMemoryStoreOptions.Create` | `SharedMemoryStoreOptions.cs` | Integration, HostedServiceIntegration |
| `SharedMemoryStoreOptions.Validate` | `SharedMemoryStoreOptions.cs` | Usage, integration, maintainers |
| `ValueLease.Release` and `ValueLease.Dispose` | `ValueLease.cs` | Usage, lifecycle, samples |
| `ValueReservation.GetSpan`, `Advance`, `Commit`, `Abort`, `Dispose` | `ValueReservation.cs` | Usage, examples, lifecycle, ZeroCopyIngest |
| `DiagnosticsSnapshot.GetFailureCount` | `DiagnosticsSnapshot.cs` | Diagnostics, errors, maintainers |

## Status Names

| Status family | Names | Public docs |
|---------------|-------|-------------|
| `StoreOpenStatus` | `Success`, `AlreadyExists`, `NotFound`, `InvalidOptions`, `IncompatibleLayout`, `UnsupportedPlatform`, `InsufficientCapacity`, `AccessDenied`, `MappingFailed`, `StoreBusy`, `OperationCanceled` | `docs/errors.md`, `docs/usage.md`, `docs/getting-started.md` |
| `StoreStatus` | `Success`, `DuplicateKey`, `NotFound`, `InvalidKey`, `KeyTooLarge`, `ValueTooLarge`, `DescriptorTooLarge`, `StoreFull`, `LeaseTableFull`, `InvalidLease`, `LeaseAlreadyReleased`, `RemovePending`, `UnsupportedPlatform`, `StoreDisposed`, `CorruptStore`, `AccessDenied`, `UnknownFailure`, `InvalidReservation`, `ReservationIncomplete`, `ReservationAlreadyCompleted`, `ReservationWriteOutOfRange`, `StoreBusy`, `OperationCanceled` | `docs/errors.md`, `docs/usage.md`, `docs/diagnostics.md`, sample READMEs |

## Contract Traceability

| Contract | Documents that must link to it |
|----------|--------------------------------|
| `specs/001-frame-memory-store/contracts/public-api.md` | `README.md`, `docs/concepts.md`, `docs/byte-encoding.md`, `docs/usage.md`, `docs/examples.md`, `docs/errors.md`, `docs/diagnostics.md`, `docs/lifecycle.md`, `docs/architecture.md`, `docs/maintainers.md` |
| `specs/001-frame-memory-store/contracts/error-taxonomy.md` | `README.md`, `docs/errors.md`, `docs/diagnostics.md`, `docs/lifecycle.md`, `docs/performance.md`, `docs/maintainers.md` |
| `specs/001-frame-memory-store/contracts/shared-memory-layout.md` | `README.md`, `docs/concepts.md`, `docs/byte-encoding.md`, `docs/examples.md`, `docs/lifecycle.md`, `docs/portability.md`, `docs/architecture.md`, `docs/maintainers.md` |
| `specs/003-zero-copy-ingest/contracts/reservation-api.md` | `docs/concepts.md`, `docs/usage.md`, `docs/examples.md`, `docs/lifecycle.md`, `docs/maintainers.md` |
| `specs/003-zero-copy-ingest/contracts/ingest-layout.md` | `docs/usage.md`, `docs/architecture.md`, `docs/portability.md`, `docs/maintainers.md` |
| `specs/003-zero-copy-ingest/contracts/diagnostics-and-errors.md` | `docs/errors.md`, `docs/diagnostics.md`, `docs/maintainers.md` |
| `specs/004-store-reliability-hardening/contracts/owner-recovery-contract.md` | `docs/lifecycle.md`, `docs/diagnostics.md`, `docs/architecture.md`, `docs/maintainers.md` |
| `specs/004-store-reliability-hardening/contracts/disposal-rollover-contract.md` | `docs/lifecycle.md`, `docs/architecture.md`, `docs/maintainers.md` |
| `specs/004-store-reliability-hardening/contracts/index-health-contract.md` | `docs/diagnostics.md`, `docs/performance.md`, `docs/architecture.md`, `docs/maintainers.md` |
| `specs/005-api-production-readiness/contracts/public-api-contract.md` | `docs/usage.md`, `docs/releases.md`, `docs/maintainers.md` |
| `specs/005-api-production-readiness/contracts/contention-configuration-contract.md` | `docs/usage.md`, `docs/errors.md`, `docs/maintainers.md` |
| `specs/005-api-production-readiness/contracts/diagnostics-integration-contract.md` | `docs/diagnostics.md`, `docs/integration.md`, `docs/maintainers.md` |
| `specs/005-api-production-readiness/contracts/reservation-memory-contract.md` | `docs/usage.md`, `docs/lifecycle.md`, `docs/maintainers.md` |

## Stale References To Reject

Documentation validation rejects stale public names or unsupported promises such
as:

- `SharedMemoryStore.SharedMemoryStore` as the current concrete store type.
- `ValueReservation.GetMemory`.
- claims that the core package has hosting, dependency injection, logging, or
  health-check dependencies.
- claims that data persists beyond process and mapping lifetime.
- claims that the package is a distributed cache.
- claims that C++ or Python bindings are currently delivered.
