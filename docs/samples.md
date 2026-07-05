# Samples

The samples are executable documentation. Run them from the repository root in
the order below when learning the package from simple to advanced. Return to
the [Documentation index](index.md) when you want a goal-based route instead of
the sample ladder.

Each sample README includes purpose and audience, concepts demonstrated,
prerequisites, command, expected output shape, non-success statuses, cleanup,
related documentation, and non-goals. The README contract is defined in
[sample-contract.md](../specs/006-improve-docs-samples/contracts/sample-contract.md).

## Prerequisites

- .NET SDK compatible with `net10.0`.
- PowerShell 7 (`pwsh`) when running repository validation scripts.
- Linux or Windows for ordinary host samples.
- Docker Engine or Docker Desktop when running the Docker shared-memory sample.

Unsupported platforms or isolated Docker profiles may return
`UnsupportedPlatform`, `NotFound`, `AccessDenied`, or `MappingFailed` instead
of the success paths shown in sample output.

## Learning Ladder

| Order | Sample | Use when | Run command |
|-------|--------|----------|-------------|
| 1 | [samples/BasicUsage/README.md](../samples/BasicUsage/README.md) | You want the smallest create, publish, acquire, release, remove, reuse, and diagnostics workflow. | `dotnet run --project samples/BasicUsage/BasicUsage.csproj -c Release` |
| 2 | [samples/FrameValue/README.md](../samples/FrameValue/README.md) | You want to model frame-shaped values while keeping descriptor and payload parsing outside the core store. | `dotnet run --project samples/FrameValue/FrameValue.csproj -c Release` |
| 3 | [samples/ZeroCopyIngest/README.md](../samples/ZeroCopyIngest/README.md) | You want direct reservation ingest, abort cleanup, segmented publish, and adapter examples. | `dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release` |
| 4 | [samples/HostedServiceIntegration/README.md](../samples/HostedServiceIntegration/README.md) | You want an application-owned lifecycle and health wrapper without adding dependencies to the core package. | `dotnet run --project samples/HostedServiceIntegration/HostedServiceIntegration.csproj -c Release` |
| 5 | [samples/DockerSharedMemory/README.md](../samples/DockerSharedMemory/README.md) | You want to validate same-host Docker containers sharing one store. | `pwsh ./scripts/validate-docker-shared-memory.ps1` |

## Basic Usage

Start here after [Getting started](getting-started.md). The sample uses
`MemoryStore.TryCreateOrOpen`, writes a canonical integer key and descriptor
through small helper methods, publishes a byte payload, acquires a `ValueLease`,
releases it, removes the key, publishes again to prove slot reuse, and prints a
diagnostic field.

Related guides:

- [Concepts](concepts.md) for store, key, descriptor, payload, lease, status,
  and diagnostics vocabulary.
- [Byte encoding](byte-encoding.md) for allocation-conscious key, descriptor,
  and payload byte guidance.
- [Usage](usage.md) for the complete consumer workflow.
- [Errors](errors.md) for expected non-success statuses.

## Frame Value

Run this after the basic sample when you need to carry application metadata.
The sample stores frame metadata in descriptor bytes and frame payload in value
bytes. It intentionally proves that the core package is frame-neutral: the
store protects byte lifetimes and reuse, while the consumer owns parsing.

Related guides:

- [Examples](examples.md) for frame-shaped values.
- [Portability](portability.md) for language-neutral layout boundaries.
- [Lifecycle](lifecycle.md) for multiple-reader and `RemovePending` behavior.

## Zero-Copy Ingest

Run this when payload length is known before all bytes arrive. The sample
demonstrates chunked writes into `ValueReservation.GetSpan()`, exact
`Advance()` progress, trusted stream reads through
`ValueReservation.DangerousGetMemory()`, `Commit()`, `Abort()`, reader
acquisition, cleanup, a pipeline adapter, and `TryPublishSegments`.

Focused commands:

```powershell
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- socket
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- pipeline
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- reader
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- segmented
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release -- abort
```

Related guides:

- [Usage](usage.md) for direct reservation and segmented publish workflows.
- [Examples](examples.md) for direct frame ingest and pipeline adapters.
- [Diagnostics](diagnostics.md) for reservation failure and recovery signals.

## Hosted Service Integration

Run this after the feature samples if your application needs lifecycle and
health integration. The sample keeps the wrapper application-owned and uses the
concrete `MemoryStore` API internally. It does not add hosting, logging,
dependency injection, options-framework, or health-check dependencies to the
core package.

Related guides:

- [Integration](integration.md) for narrow wrapper boundaries.
- [Lifecycle](lifecycle.md) for startup, shutdown, recovery, and disposal.
- [Maintainers](maintainers.md) for the rule that optional adapters must not
  become hidden core package dependencies.

## Docker Shared Memory

Run this when containerized producers and readers need to participate in the
same host-local store. The sample includes a supported Compose profile that
shares IPC and process-liveness capabilities, plus an isolated negative profile
that must fail clearly instead of silently using a different store.

Related guides:

- [Portability](portability.md) for the same-host Docker support boundary.
- [Lifecycle](lifecycle.md) for recovery and cleanup behavior.
- [Diagnostics](diagnostics.md) for failure counters and recovery reports.

## Validation

Build all samples through the solution:

```powershell
dotnet build SharedMemoryStore.slnx -c Release
```

Run the full validation path from
[quickstart.md](../specs/006-improve-docs-samples/quickstart.md) before
release:

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
pwsh ./scripts/validate-docker-shared-memory.ps1
```
