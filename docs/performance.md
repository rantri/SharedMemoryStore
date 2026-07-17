# Performance Scope

SharedMemoryStore uses one SMS2 lock-free protocol in C#, C++, and Python.
After a handle opens, publish, reserve, acquire, release, remove, recovery, and
diagnostics do not acquire the platform lifecycle lock or a store-wide
operation lock. The implementation uses mapped atomics, bounded scans,
cooperative helping, and process-local backoff.

This is a progress and architecture guarantee, not a universal latency or
throughput number. Lock-free means the system continues to make progress; an
individual operation can still return `StoreBusy` after its configured budget
expires.

The normative rules are in the
[protocol conformance contract](../specs/010-lock-free-only-multilang/contracts/protocol-conformance.md).

## Measured Areas

The managed benchmark project under
[`benchmarks/SharedMemoryStore.Benchmarks/`](../benchmarks/SharedMemoryStore.Benchmarks/)
covers representative lifecycle and data-path work, including:

- create/open and disposal;
- contiguous and segmented publication;
- direct reservation ingest;
- acquire/release and removal/reuse;
- recovery and lifecycle stress; and
- throughput, allocation, and failure latency.

Native qualification and the interoperability suite add cross-process,
cross-runtime, collision, overflow, helping, recovery, and cold-lock isolation
evidence. Results are environment-specific; keep the command, build, host, and
workload details with every published measurement.

## Running Managed Benchmarks

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release
```

Focused ingest benchmarks:

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --filter *DirectIngest*
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --filter *SegmentedPublish*
```

Sustained-throughput and direct-allocation validation:

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --validation sustained-throughput
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --validation direct-allocation
```

Record the OS, architecture, CPU, compiler/runtime versions, package version,
the five-field protocol identity, configured capacities, payload and descriptor
sizes, participant/producer/reader counts, operation budget, final statuses,
and exact command.

## Capacity and Contention

Capacity is fixed at create time:

- `SlotCount` covers published values plus records awaiting reservation,
  removal, or reclamation.
- `LeaseRecordCount` covers concurrent read leases.
- `ParticipantRecordCount` covers concurrently open handles across all
  runtimes.
- `MaxKeyBytes`, `MaxDescriptorBytes`, and `MaxValueBytes` bound inline data.
- the derived primary and overflow directory capacities bound collision-heavy
  key placement.

`StoreFull`, `LeaseTableFull`, and `ParticipantTableFull` report distinct
capacity limits. `StoreBusy` reports an exhausted retry/revalidation/helping
budget, not a held hot-path mutex. High collision workloads can increase CAS
retries, helping, spill occupancy, and overflow scans before any capacity
status occurs.

Use [Diagnostics](diagnostics.md) to distinguish these cases. In particular,
compare free and active slot/lease/participant counts with primary directory
occupancy, spilled buckets, overflow occupancy, CAS retries, helped
transitions, and contention-budget exhaustion.

## Allocation Expectations

The managed implementation is designed to avoid per-operation managed heap
allocation in ordinary warmed hot paths. C++ uses caller-owned RAII objects and
views. Python wrapper objects follow Python lifetime conventions while payload
and descriptor views remain borrowed mapped views subject to the documented
lease/reservation lifetime rules.

These expectations do not remove setup costs: opening validates the whole
layout, registers a participant, and may allocate process-local proof or
scratch state. Diagnostics formatting, logging, application parsing, and
copying a borrowed view into application-owned bytes are caller costs.

## Platform Assumptions

The protocol is qualified only on x86-64 little-endian Windows and Linux where
aligned 64-bit mapped atomics are always lock-free and the required mapping,
cold lifecycle, and owner-evidence facilities are available. Same-host Docker
sharing requires the configuration described in [Portability](portability.md).

Do not infer performance or even support from a successful compile on another
architecture, byte order, filesystem, container arrangement, or emulator.

## Not Claimed

The project does not promise:

- a fixed throughput or latency percentile on unmeasured hardware;
- starvation freedom for each individual operation;
- cross-host behavior or persistence after mapping lifetime;
- application-specific parsing performance;
- protection from malicious same-host writers with mapping access; or
- stable benchmark numbers across protocol or implementation changes.

## Release Review

When adding or changing a performance claim, update this page,
[Release preparation](releases.md), [Maintainers](maintainers.md), and
[CHANGELOG.md](../CHANGELOG.md) when the change is release-facing. Preserve raw
results and qualification logs that identify the tested artifacts and protocol
identity.
