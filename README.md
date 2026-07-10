# SharedMemoryStore

SharedMemoryStore is a monorepo for bounded named shared-memory key-value
storage. Its .NET, native C++, and Python distributions store opaque byte keys,
optional descriptor bytes, and immutable payload bytes in one versioned
memory-mapped protocol so same-host processes can exchange data without copying
payloads through a broker process.

Distribution identities:

- NuGet: `SharedMemoryStore` `1.0.1`, targeting `net10.0` with .NET BCL
  runtime dependencies only.
- CMake: `SharedMemoryStore` `0.1.0`, exposing a C++20 RAII API and fixed-width
  C ABI `1.0` over the native shared library.
- Python: `shared-memory-store` `0.1.0`, requiring Python 3.10 or newer and
  using standard-library `ctypes` with the packaged native library.
- Shared protocol: mapped layout `1.2`, resource naming `1`, little-endian
  64-bit Windows and Linux targets.
- License: MIT, see the [license file](LICENSE).

The managed `1.0.0` line establishes the production .NET API contract. The
native and Python `0.1.0` lines are independently versioned initial
distributions; they do not change or ship inside the NuGet package. Linux and
Windows are implementation targets. Same-host Linux Docker containers require
shared IPC, owner-liveness, permission, and shared-memory capacity capabilities.
See [Portability](docs/portability.md) and
[Compatibility metadata](protocol/compatibility.json) before combining
independently released distributions.

## What It Provides

The shared lifecycle implemented by the three public APIs supports:

- create or open a named store with explicit capacity limits.
- publish immutable value bytes and optional descriptor bytes under an opaque
  byte key.
- acquire an owning lease, read descriptor and value views, and release or
  dispose the lease exactly once.
- remove values and reuse slots after active readers release their leases.
- reserve store-owned payload memory for direct length-delimited frame ingest,
  advance exact write progress, and commit atomically.
- publish segmented buffered payloads, including .NET
  `ReadOnlySequence<byte>`, without a temporary full-payload array.
- abort or explicitly recover incomplete reservations without exposing partial
  bytes to readers.
- run owner-controlled stale lease recovery when enabled.
- inspect caller-formatted diagnostics snapshots without library console output,
  including lease recovery results and key-index tombstone health.

The native core implements the mapped protocol and Windows/Linux resource
mechanisms once. The C++ API wraps the C ABI with move-only RAII stores, leases,
and reservations. The Python package uses `ctypes` and context-managed objects
over that same ABI; it does not maintain a second protocol state machine.

The store does not parse frame headers, own application schemas, provide a
cross-host cache, persist data beyond process and mapping lifetime, or turn
Docker into distributed storage.

## First Use

Start with [Getting started](docs/getting-started.md). It separates the NuGet,
CMake, and wheel workflows and explains how processes select the same named
store. The managed package workflow remains:

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

Native build and test entry point:

```powershell
pwsh ./scripts/validate-native.ps1 -Configuration Release
```

Python wheel build and installed-sample entry point:

```powershell
python -m pip install build
python -m build --wheel
python -m venv artifacts/python-consumer
artifacts/python-consumer/Scripts/python -m pip install (Get-ChildItem dist/*.whl | Select-Object -First 1)
artifacts/python-consumer/Scripts/python samples/PythonBasicUsage/main.py
```

On Linux, use `artifacts/python-consumer/bin/python`. The Python sample must run
from an installed wheel because the native loader deliberately searches only
the package directory, never the current directory or system library path.

## Documentation

- [Documentation index](docs/index.md): complete table of contents by audience.
- [Getting started](docs/getting-started.md): NuGet, CMake, wheel, minimal
  workflow, and interoperability setup.
- [Concepts](docs/concepts.md): store, name, key, descriptor, payload, slot,
  lease, reservation, wait policy, status, diagnostics, recovery, capacity,
  lifecycle, portability, and package contract vocabulary.
- [Byte encoding](docs/byte-encoding.md): canonical key, descriptor, and
  payload byte layouts with allocation-conscious helper patterns.
- [Usage guide](docs/usage.md): create/open, publish, reserve, segmented publish,
  acquire, release, remove, reuse, diagnostics, recovery, and dispose.
- [Examples](docs/examples.md): basic values, frame-shaped values, direct
  reservation ingest, segmented payloads, diagnostics, waits, and error
  handling.
- [Errors and statuses](docs/errors.md): deterministic status outcomes and
  troubleshooting.
- [Diagnostics](docs/diagnostics.md): snapshot fields and consumer-owned
  observability.
- [Lifecycle](docs/lifecycle.md): store ownership, leases, removal, stale
  recovery, abnormal termination, and cleanup.
- [Integration](docs/integration.md): optional lifecycle, health, hosting, and
  narrow-interface boundaries outside the core package.
- [Performance scope](docs/performance.md): measured scope and unmeasured
  claims.
- [Portability](docs/portability.md): distribution versions, Linux, Windows,
  same-host Docker, layout compatibility, and cross-runtime constraints.
- [Samples](docs/samples.md): ordered runnable sample ladder from minimal usage
  through frame values, zero-copy ingest, optional hosted integration, and
  same-host Docker validation.
- [Architecture](docs/architecture.md): managed and native dependency
  direction, source areas, storage, lifecycle, synchronization, recovery, and
  diagnostics.
- [Maintainers](docs/maintainers.md): documentation update rules, validation
  commands, contract boundaries, performance evidence, and release impact.
- [Packaging](docs/packaging.md): NuGet, CMake, wheel, compatibility metadata,
  release notes, and clean consumer validation.
- [Release preparation](docs/releases.md): maintainer checks before publication.

Detailed behavior sources:

- [Public API contract](specs/001-frame-memory-store/contracts/public-api.md)
- [Error taxonomy contract](specs/001-frame-memory-store/contracts/error-taxonomy.md)
- [Shared-memory layout contract](specs/001-frame-memory-store/contracts/shared-memory-layout.md)
- [Reservation API contract](specs/003-zero-copy-ingest/contracts/reservation-api.md)
- [Ingest layout contract](specs/003-zero-copy-ingest/contracts/ingest-layout.md)
- [Reservation diagnostics and errors](specs/003-zero-copy-ingest/contracts/diagnostics-and-errors.md)
- [Owner recovery hardening contract](specs/004-store-reliability-hardening/contracts/owner-recovery-contract.md)
- [Disposal and rollover hardening contract](specs/004-store-reliability-hardening/contracts/disposal-rollover-contract.md)
- [Index health hardening contract](specs/004-store-reliability-hardening/contracts/index-health-contract.md)
- [Production public API contract](specs/005-api-production-readiness/contracts/public-api-contract.md)
- [Contention configuration contract](specs/005-api-production-readiness/contracts/contention-configuration-contract.md)
- [Diagnostics integration contract](specs/005-api-production-readiness/contracts/diagnostics-integration-contract.md)
- [Reservation memory contract](specs/005-api-production-readiness/contracts/reservation-memory-contract.md)
- [Language-neutral protocol](protocol/README.md)
- [Native C ABI contract](specs/008-cpp-python-implementations/contracts/native-c-api.md)
- [C++ API contract](specs/008-cpp-python-implementations/contracts/cpp-api.md)
- [Python API contract](specs/008-cpp-python-implementations/contracts/python-api.md)
- [Interoperability contract](specs/008-cpp-python-implementations/contracts/interoperability.md)
- [Distribution packaging contract](specs/008-cpp-python-implementations/contracts/packaging.md)

Runnable samples:

- [Basic usage sample](samples/BasicUsage/README.md)
- [Frame value sample](samples/FrameValue/README.md)
- [Zero-copy ingest sample](samples/ZeroCopyIngest/README.md)
- [Hosted service integration sample](samples/HostedServiceIntegration/README.md)
- [Docker shared-memory sample](samples/DockerSharedMemory/README.md)
- [C++ basic usage sample](samples/CppBasicUsage/README.md)
- [Python basic usage sample](samples/PythonBasicUsage/README.md)

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
pwsh ./scripts/validate-docs.ps1
dotnet build SharedMemoryStore.slnx -c Release
dotnet run --project samples/BasicUsage/BasicUsage.csproj -c Release
dotnet run --project samples/FrameValue/FrameValue.csproj -c Release
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release
dotnet run --project samples/HostedServiceIntegration/HostedServiceIntegration.csproj -c Release
dotnet run --project samples/DockerSharedMemory/DockerSharedMemory.csproj -c Release -- all
pwsh ./scripts/validate-package-consumption.ps1
dotnet test SharedMemoryStore.slnx -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
pwsh ./scripts/validate-cross-platform.ps1 -SkipDocker
pwsh ./scripts/validate-docker-shared-memory.ps1
pwsh ./scripts/validate-native.ps1 -Configuration Release
pwsh ./scripts/validate-python.ps1 -Configuration Release
pwsh ./scripts/validate-interoperability.ps1 -Configuration Release -Stress
```

Documentation changes must keep package metadata, README content, release notes,
support policy, security policy, compatibility metadata, and contract links
aligned across managed `1.0.1`, native `0.1.0`, Python `0.1.0`, ABI `1.0`, and
layout `1.2`.
