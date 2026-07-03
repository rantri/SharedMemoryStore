# SharedMemoryStore

SharedMemoryStore is a `net10.0` library package for bounded named
shared-memory key-value storage. It stores opaque byte keys, optional descriptor
bytes, and immutable payload bytes in a memory-mapped region so producers and
readers can exchange data without copying payloads through a broker process.

Package identity:

- PackageId: `SharedMemoryStore`
- Version: `1.0.0`
- Target framework: `net10.0`
- License: MIT, see the [license file](LICENSE)
- Runtime dependencies: .NET BCL only

The `1.0.0` package establishes the production public API contract. Windows x64
named memory-mapped files are the first validated runtime target. C++ and
Python are future portability audiences, not current bindings.

## What It Provides

The initial public contract supports:

- create or open a named store with explicit capacity limits.
- publish immutable value bytes and optional descriptor bytes under an opaque
  byte key.
- acquire a `ValueLease`, read descriptor and value spans, and release or
  dispose the lease exactly once.
- remove values and reuse slots after active readers release their leases.
- reserve store-owned payload memory for direct length-delimited frame ingest,
  advance exact write progress, and commit atomically.
- publish segmented buffered payloads through `ReadOnlySequence<byte>` without a
  temporary full-payload array.
- abort or explicitly recover incomplete reservations without exposing partial
  bytes to readers.
- run owner-controlled stale lease recovery when enabled.
- inspect caller-formatted diagnostics snapshots without library console output,
  including lease recovery results and key-index tombstone health.

The store does not parse frame headers, own application schemas, provide a
distributed cache, persist data after process and mapping lifetime, or promise
cross-platform support beyond the documented Windows-first validation scope.

## First Use

For package consumers, start with [Getting started](docs/getting-started.md) and
the [Usage guide](docs/usage.md). A local package source workflow is documented
because this prerelease repository may be consumed before a public NuGet publish.

```powershell
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
dotnet new console -f net10.0 -n SharedMemoryStore.Tryout -o artifacts/tryout
dotnet add artifacts/tryout/SharedMemoryStore.Tryout.csproj package SharedMemoryStore --source artifacts/package
```

Minimal workflow:

```csharp
using SharedMemoryStore;

var options = new SharedMemoryStoreOptions
{
    Name = $"sms-{Guid.NewGuid():N}",
    OpenMode = OpenMode.CreateOrOpen,
    SlotCount = 2,
    MaxValueBytes = 64,
    MaxDescriptorBytes = 16,
    MaxKeyBytes = 16,
    LeaseRecordCount = 4,
    EnableLeaseRecovery = true,
    TotalBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(2, 64, 16, 16, 4)
};

var open = MemoryStore.TryCreateOrOpen(options, out var store);
if (open != StoreOpenStatus.Success || store is null)
{
    return;
}

using (store)
{
    var status = store.TryPublish([1, 2, 3], [4, 5, 6], [9]);
    status = store.TryAcquire([1, 2, 3], out var lease);
    var firstByte = lease.ValueSpan[0];
    status = lease.Release();
    status = store.TryRemove([1, 2, 3]);

    status = store.TryReserve([4], 3, [1], out var reservation);
    new byte[] { 7, 8, 9 }.CopyTo(reservation.GetSpan());
    status = reservation.Advance(3);
    status = reservation.Commit();
}
```

Expected operational failures are returned as `StoreOpenStatus` or
`StoreStatus` values. See [Errors and statuses](docs/errors.md) for duplicate
keys, missing keys, full stores, oversized values, invalid leases, unsupported
platforms, stale leases, cleanup failures, and version mismatches.
Current-process lease recovery skips other live owner processes, disposal races
return documented statuses or empty token views, and slot lifecycle identity is
safe across generation rollover.

## Documentation

- [Documentation index](docs/index.md): complete table of contents by audience.
- [Getting started](docs/getting-started.md): install, local package source,
  minimal workflow, and expected statuses.
- [Usage guide](docs/usage.md): create/open, publish, reserve, segmented publish,
  acquire, release, remove, reuse, diagnostics, recovery, and dispose.
- [Errors and statuses](docs/errors.md): deterministic status outcomes and
  troubleshooting.
- [Diagnostics](docs/diagnostics.md): snapshot fields and consumer-owned
  observability.
- [Lifecycle](docs/lifecycle.md): store ownership, leases, removal, stale
  recovery, abnormal termination, and cleanup.
- [Integration](docs/integration.md): optional lifecycle, health, hosting, and
  narrow-interface boundaries outside the core package.
- [Examples](docs/examples.md): basic workflow, error handling, and
  frame-shaped values.
- [Performance scope](docs/performance.md): measured scope and unmeasured
  claims.
- [Portability](docs/portability.md): .NET 10 baseline, Windows-first
  validation, layout compatibility, and future C++/Python constraints.
- [Packaging](docs/packaging.md): package metadata, package README, release
  notes, and clean consumer validation.
- [Release preparation](docs/releases.md): maintainer checks before publication.

Detailed behavior sources:

- [Public API contract](specs/001-frame-memory-store/contracts/public-api.md)
- [Error taxonomy contract](specs/001-frame-memory-store/contracts/error-taxonomy.md)
- [Shared-memory layout contract](specs/001-frame-memory-store/contracts/shared-memory-layout.md)
- [Reservation API contract](specs/003-zero-copy-ingest/contracts/reservation-api.md)
- [Ingest layout contract](specs/003-zero-copy-ingest/contracts/ingest-layout.md)
- [Reservation diagnostics and errors](specs/003-zero-copy-ingest/contracts/diagnostics-and-errors.md)

Runnable samples:

- [Basic usage sample](samples/BasicUsage/README.md)
- [Frame value sample](samples/FrameValue/README.md)
- [Zero-copy ingest sample](samples/ZeroCopyIngest/README.md)
- [Hosted service integration sample](samples/HostedServiceIntegration/README.md)

## Project Policies

- [Contributing](CONTRIBUTING.md): setup, validation, compatibility review, and
  pull request expectations.
- [Code of conduct](CODE_OF_CONDUCT.md): project-specific conduct expectations.
- [Support](SUPPORT.md): questions, bugs, unsupported scenarios, and best-effort
  prerelease support.
- [Security](SECURITY.md): private vulnerability reporting guidance.
- Issue templates:
  [bug report](.github/ISSUE_TEMPLATE/bug_report.yml),
  [documentation issue](.github/ISSUE_TEMPLATE/documentation.yml), and
  [feature request](.github/ISSUE_TEMPLATE/feature_request.yml).
- [Pull request template](.github/pull_request_template.md): review checklist
  for behavior, API, package, validation, documentation, compatibility,
  security, support, and release-note impact.
- [Changelog](CHANGELOG.md): reverse-chronological package and documentation
  history.
- [Release notes](docs/releases.md): release readiness checklist and package
  notes alignment.

## Local Validation

```powershell
scripts/validate-docs.ps1
scripts/validate-package-consumption.ps1
dotnet test -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

Documentation changes must keep package metadata, README content, release notes,
support policy, security policy, and contract links aligned with the current
`1.0.0` package behavior.
