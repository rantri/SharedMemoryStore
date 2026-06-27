# Implementation Plan: Shared Memory Value Store

**Branch**: `001-frame-memory-store` | **Date**: 2026-06-27 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/001-frame-memory-store/spec.md`

## Summary

Build SharedMemoryStore as a reusable C#/.NET 10 NuGet package that exposes a
bounded, named shared-memory key-value store for opaque binary values. The
initial implementation will use a versioned memory-mapped region containing a
fixed header, shared index, fixed-size reusable value slots, optional descriptor
bytes, and a lease registry. Producers publish values into preallocated slots;
readers acquire struct-based leases that expose spans over shared memory without
copying payload bytes; remove/release operations reclaim slots only when no
active lease protects them.

The first production scenario stores about 1.3 MB frame-shaped values, but the
core library remains frame-agnostic. Frame header, metadata, and payload
interpretation belong to consumer-owned descriptor/value layout rules rather
than store-specific APIs.

## Technical Context

**Language/Version**: C# targeting `net10.0`; public contracts documented so
future C++ and Python clients can implement the same byte layout, key rules,
lifecycle states, lease semantics, and error taxonomy.

**Primary Dependencies**: Runtime package uses only the .NET BCL, primarily
`System.IO.MemoryMappedFiles`, `System.Threading`, `System.Runtime.InteropServices`,
and span/buffer primitives. Test and validation projects may use xUnit.net,
Microsoft.NET.Test.Sdk, and BenchmarkDotNet; these are not runtime package
dependencies.

**Storage**: Named OS memory-mapped region. All store metadata, index entries,
slot metadata, value bytes, descriptor bytes, and lease records live inside the
configured mapped region. No database, file format beyond the memory-mapped
backing store, or external broker is used.

**Testing**: `dotnet test` with unit, contract, and integration coverage.
Benchmark validation uses BenchmarkDotNet and custom stress scenarios under
Release configuration.

**Target Platform**: .NET 10 supported platforms with named memory-mapped file
support. Windows is the first validated platform for the production frame
scenario. Unsupported or partially supported platforms return deterministic
unsupported-platform outcomes during store creation/opening.

**Project Type**: Reusable NuGet package/library with sample consumer projects,
documentation, public XML comments, and package metadata.

**Performance Goals**: After initialization and warm-up, publish, acquire,
release, remove, and slot reuse operations allocate 0 managed heap bytes per
operation. The benchmark target is at least 500 publishes per second for 1.3 MB
values for 60 seconds, 100,000 publish/acquire/release/remove cycles with one
producer and four readers, and one million publish/remove/reuse cycles with
committed memory remaining within 1% of configured capacity plus documented
fixed overhead.

**Constraints**: Bounded configured capacity; fixed maximum key, descriptor, and
value sizes; no payload copies for readers; immutable published value contents;
no direct console output from library code; no hidden background cleanup;
deterministic results for duplicate keys, missing keys, oversized values, full
capacity, invalid releases, and unsupported platforms; all shared layout changes
versioned.

**Scale/Scope**: Initial package targets same-host producers and consumers in a
trusted service boundary. The design supports configurable slot counts and value
sizes, with first validation centered on approximately 1.3 MB values, one
producer, four concurrent readers, and a single named store. Multiple stores are
allowed by name; cross-host transport is out of scope.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- Library/package first: PASS. The planned deliverable is a standalone NuGet
  package with reusable APIs and no frame-specific operations.
- NuGet deliverable: PASS. Package metadata, XML documentation, examples,
  contract docs, and clean-project consumption validation are planned.
- Stable contracts: PASS. Public API, memory layout, statuses, lifecycle rules,
  diagnostics, and semantic version impact are captured in `contracts/`.
- .NET 10 baseline with portability: PASS. Implementation targets `net10.0`
  while documenting byte layout, key rules, lifecycle semantics, and future
  C++/Python constraints.
- Test coverage: PASS. Unit, contract, integration, concurrency, resource
  cleanup, package-consumption, and benchmark validation are planned.
- Dependency discipline: PASS. Runtime package uses the BCL only. Test and
  benchmark dependencies stay outside the runtime package.
- Diagnostics and resource ownership: PASS. Diagnostics are consumer-controlled
  through result codes, snapshots, and optional observer hooks; lifecycle and
  cleanup responsibilities are documented.

## Project Structure

### Documentation (this feature)

```text
specs/001-frame-memory-store/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── public-api.md
│   ├── shared-memory-layout.md
│   └── error-taxonomy.md
└── tasks.md
```

### Source Code (repository root)

```text
src/
└── SharedMemoryStore/
    ├── SharedMemoryStore.csproj
    ├── SharedMemoryStoreOptions.cs
    ├── SharedMemoryStore.cs
    ├── ValueLease.cs
    ├── StoreStatus.cs
    ├── Diagnostics/
    ├── Interop/
    ├── Layout/
    ├── Leasing/
    ├── Options/
    └── Slots/

tests/
├── SharedMemoryStore.UnitTests/
├── SharedMemoryStore.ContractTests/
└── SharedMemoryStore.IntegrationTests/

benchmarks/
└── SharedMemoryStore.Benchmarks/

samples/
├── BasicUsage/
└── FrameValue/

docs/
├── lifecycle.md
├── packaging.md
└── portability.md
```

**Structure Decision**: Create one runtime package under `src/SharedMemoryStore`
and keep tests, benchmarks, samples, and docs outside the package. The runtime
project owns public APIs, memory layout primitives, slot allocation, lease
tracking, and diagnostics. Contract tests verify public API and shared layout
behavior. Integration tests validate multi-store/process-style usage,
concurrency, stale lease recovery, and slot reuse. Benchmarks are separate so
BenchmarkDotNet does not become a runtime dependency.

## Complexity Tracking

No constitution violations are planned.

## Phase 0 Research Summary

See [research.md](research.md).

Key decisions:
- Use .NET BCL memory-mapped files as the runtime shared-memory primitive.
- Use fixed-size reusable slots for the first implementation to make capacity,
  allocation, and reuse behavior deterministic.
- Store a language-neutral, versioned layout in shared memory and expose a
  C#-friendly API over it.
- Use struct-based lease tokens and status/result enums to avoid steady-state
  managed heap allocations.
- Keep cleanup explicit and consumer-controlled; no hidden background workers.

## Phase 1 Design Summary

See [data-model.md](data-model.md), [contracts/public-api.md](contracts/public-api.md),
[contracts/shared-memory-layout.md](contracts/shared-memory-layout.md),
[contracts/error-taxonomy.md](contracts/error-taxonomy.md), and
[quickstart.md](quickstart.md).

Post-design constitution check remains PASS:
- The API contract is package-first and frame-neutral.
- Semantic version impact is documented as an initial public package contract.
- Runtime dependencies remain BCL-only.
- Validation includes unit, contract, integration, package consumption,
  concurrency, resource cleanup, and benchmark scenarios.
- Diagnostics and lifecycle ownership are consumer-controlled and documented.
