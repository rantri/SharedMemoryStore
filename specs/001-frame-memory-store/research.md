# Phase 0 Research: Shared Memory Value Store

## Decision: Use .NET BCL memory-mapped files for the runtime store

**Rationale**: `System.IO.MemoryMappedFiles` is available in the .NET runtime
and supports named shared memory on supported platforms. It avoids a runtime
package dependency and maps directly to the library's same-host shared-memory
requirements. The store can reserve one mapped region for the header, index,
slot metadata, descriptor bytes, payload bytes, and lease registry.

**Alternatives considered**:
- Memory-only static process store: rejected because independent services must
  share data across process boundaries.
- Network transport or broker: rejected because the feature requires shared
  memory and zero-copy reader access, not cross-host messaging.
- Third-party shared-memory library: rejected for the initial package because
  runtime dependency risk is unnecessary until a specific platform gap is found.

## Decision: Start with fixed-size reusable value slots

**Rationale**: Fixed-size slots make the configured maximum value size, maximum
descriptor size, slot count, capacity pressure, and slot reuse behavior
predictable. They also avoid allocator metadata growth and fragmentation during
steady-state operation. This fits the first production workload of repeated
about-1.3 MB frame-shaped values.

**Alternatives considered**:
- Variable-size allocator inside the mapped region: rejected for the first
  release because it adds fragmentation, compaction, and corruption risks that
  are not needed to satisfy the current feature.
- One mapping per value: rejected because per-value mapping creation and
  disposal would work against high-rate publishing and bounded capacity.
- Managed pooled arrays: rejected because readers need direct shared-memory
  access across services.

## Decision: Store a versioned language-neutral memory layout

**Rationale**: The feature requires future C++ and Python portability. The
mapped region therefore needs explicit magic, version, endian, alignment,
capacity, state, key, descriptor, payload, generation, and lease semantics. C#
can provide a high-level API, but the authoritative interoperability contract is
the byte layout and lifecycle state machine.

**Alternatives considered**:
- Treat the layout as a private C# implementation detail: rejected because it
  would make future non-.NET clients depend on reverse engineering or a breaking
  contract rewrite.
- Serialize metadata with a general-purpose format: rejected because parsing and
  allocation costs conflict with the steady-state performance requirement.

## Decision: Use byte keys with fixed maximum length and inline shared index entries

**Rationale**: Byte keys are language-neutral and avoid forcing string encoding
rules into the core store. A fixed maximum key length stored inline in shared
metadata lets lookup, duplicate detection, and removal run without managed
dictionary allocations after warm-up. UTF-8 string helpers may be added as
convenience APIs, but the allocation-free contract belongs to byte-key APIs.

**Alternatives considered**:
- Managed `Dictionary<string, ...>` index: rejected because it is process-local
  and allocates during updates.
- Variable-length key heap inside shared memory: rejected for the first release
  because it introduces allocator complexity beyond the current requirements.
- Numeric-only keys: rejected because producers and consumers need general
  keyed values.

## Decision: Represent operation outcomes with deterministic status values

**Rationale**: Duplicate keys, missing values, full capacity, oversized values,
unsupported platforms, stale leases, invalid releases, and disposed stores must
return documented outcomes. Status enums and small result structs avoid
exceptions for expected conditions and support zero-allocation steady-state
operation. Exceptions remain appropriate for programmer errors outside the
documented operation contract, such as invalid options during initialization.

**Alternatives considered**:
- Throw exceptions for all failure cases: rejected because expected store
  pressure and lookup failures must be fast and allocation-free.
- Return booleans only: rejected because callers need deterministic diagnostics
  and error taxonomy.

## Decision: Use generation-checked struct leases and shared reference counts

**Rationale**: A lease records store identity, slot index, slot generation, and a
lease record id. Acquire increments a shared usage count only when the slot is
published and the generation matches. Release decrements exactly once through
the lease registry. Slot reuse requires state removal and usage count zero,
preventing use-after-release and stale lease reuse.

**Alternatives considered**:
- Managed lease classes: rejected for the allocation-free steady-state
  requirement.
- Reader-owned copies: rejected because readers must access payload bytes
  without copying.
- No generation check: rejected because a stale lease could refer to a reused
  slot.

## Decision: Keep cleanup explicit and consumer-controlled

**Rationale**: The constitution prohibits hidden background work unless
justified. The store owner should control cleanup and stale-lease recovery using
explicit APIs and diagnostics. Lease records include enough process/owner
identity for a recovery operation to detect and release abandoned leases when
platform support allows it. Platforms that cannot support the required
abandoned-lease checks return deterministic unsupported recovery statuses.

**Alternatives considered**:
- Background watchdog thread inside the library: rejected because it adds hidden
  work, process-wide behavior, and shutdown complexity.
- Ignore process termination while holding leases: rejected because the spec
  requires cleanup expectations after abnormal termination.

## Decision: Use BenchmarkDotNet only in the benchmark project

**Rationale**: BenchmarkDotNet provides allocation and throughput measurement
for the success criteria without becoming a runtime dependency. Benchmarks run
under Release configuration after warm-up and document hardware, OS, SDK, slot
configuration, value size, and reader count.

**Alternatives considered**:
- Hand-rolled timing only: rejected because allocation and throughput evidence
  should be repeatable and comparable.
- Include benchmarking hooks in the runtime package: rejected because diagnostics
  must stay consumer-controlled and dependency-conscious.
