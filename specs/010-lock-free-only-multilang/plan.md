# Implementation Plan: Lock-Free-Only Multi-Language Store

**Branch**: `codex/010-lock-free-only-multilang` | **Date**: 2026-07-16 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/010-lock-free-only-multilang/spec.md`

## Summary

Make mapped layout 2.0 (`SMS2`, required-feature mask `7`) the repository's one
current shared-memory protocol. Remove the C# legacy/profile surface and the
layout-v1.2 data engine, convert the public C# facade to an always-present engine
boundary, and faithfully port the validated SMS2 control codecs, participant,
directory, slot, lease, reclamation, recovery, corruption, and bounded-operation
state machines to a modular C++20 native engine. Expose that engine through a
breaking C ABI 2.0 and RAII C++ API; bind Python to the packaged native ABI so
Python owns ecosystem validation and borrowed-view lifetime without attempting
shared-memory atomics in Python code. Replace v1 rejection tests and profile
comparisons with one canonical fixture set, all nine ordered C#/C++/Python
interoperability pairs, deterministic pause/crash/reuse tests, raw
cross-process atomic tests, and absolute lock-free release gates on Windows x64
and Linux x64.

## Architectural Frame

The architectural problem is to maintain one stable cross-process protocol while
allowing language APIs, compiler atomic mechanisms, operating-system lifecycle
resources, and release tooling to evolve independently. The mapped bytes and
state transitions are the stable center; no language binding may reinterpret or
simplify them.

### Volatility Axes

- **Mapped protocol and feature set** changes only through an explicit protocol
  revision. A change affects every runtime and fixture and is triggered by a
  correctness or capacity requirement that cannot be encoded compatibly.
- **Concurrency algorithms and compiler atomics** change for correctness,
  performance, or toolchain support. They affect one implementation but must
  preserve the canonical transition and memory-order contract.
- **Language API and lifetime adapters** change with ecosystem ergonomics and
  packaging. They must not change mapped ownership, status, or visibility.
- **Platform cold lifecycle and owner classification** change with Windows/Linux
  resource behavior, containers, kernels, and filesystems. They must not leak
  into key-directory or value algorithms.
- **Qualification and diagnostics** change as new failure schedules and
  performance targets are added. They observe implementations but are never a
  correctness dependency.

### Boundaries and Dependency Direction

```text
C# facade       C ABI / C++ RAII       Python context wrappers
     |                  |                        |
     |                  +----------+-------------+
     |                             |
     v                             v
C# SMS2 engine                Native SMS2 engine
     |                             |
     +--------> canonical protocol contract <---+
                    |          |
                    |          +--> mapped 64-bit atomic adapter
                    +-------------> fixed layout/control codecs

C# and native engines --> platform cold-open/liveness adapters
fixtures/tests/agents --> public APIs + deterministic checkpoint seams
```

- The **protocol contract** owns byte order, sizes, offsets, codecs, states,
  memory orders, feature masks, hash/equality, and corruption rules. It must not
  know a language object, platform handle, diagnostic presentation, or test
  scheduler.
- Each **engine** owns operation orchestration, helping, retry budgets, and local
  token validation. It depends on protocol primitives and abstract platform
  identity/lifecycle services.
- Each **platform adapter** owns mapping discovery, creation disposition, cold
  gate ordering, process identity, Linux namespace/anchor evidence, and bounded
  cleanup. It must not know keys or value state machines.
- Each **language facade** owns public API shape, argument validation, local
  disposal, and borrowed-view lifetime. It must not perform or emulate mapped
  atomic transitions.
- **Validation** depends on all public surfaces and fixtures; production code
  never depends on agents, checkpoints, benchmarks, or validation scripts.

### Change-Scenario Stress Test

- **Protocol revision**: a future layout 3 adds a field. Blast radius is limited
  to protocol codecs/fixtures plus an engine implementation in each language;
  public lifetime adapters remain stable. Debt accumulates if offsets are copied
  into bindings instead of generated/verified from the canonical manifest.
- **Integration replacement**: Python changes from `ctypes` to another FFI.
  Blast radius is the Python loader/bindings only because C ABI handles and
  mapped semantics remain stable. Debt accumulates if Python learns record
  offsets or participant algorithms.
- **Scale increase**: participant or slot limits require wider identities. This
  is a mapped-protocol revision, not a local optimization. Keeping codecs in one
  boundary prevents silently weakening ABA fences in only one runtime.
- **New platform**: Linux ARM64 is qualified later. Blast radius is the mapped
  atomic and platform lifecycle adapters plus platform evidence; the protocol is
  unchanged only if all required atomic/memory-order gates pass.

## Technical Context

**Language/Version**: C# 14 on .NET 10; C++20 with fixed-width C ABI 2.0;
Python 3.10+ using standard-library `ctypes` over the packaged native library.

**Primary Dependencies**: .NET base class library; C++ standard library and
existing Windows/Linux system APIs; libc on Linux; Python standard library at
runtime. CMake 3.20+ and scikit-build-core 1.x remain build-only dependencies.
No new runtime package or broker dependency.

**Storage**: Fixed-capacity named shared memory using only layout 2.0 magic
`SMS2`, resource protocol 2, little-endian naturally aligned 64-bit atomic
control words, and required-feature mask `7`. Layout 1.2 is rejected and never
created or converted.

**Testing**: xUnit managed unit/contract/integration/linearizability/package
suites; dependency-free CTest native unit and process tests; Python standard
library unit/package tests; cross-runtime JSON-lines agents; canonical binary
fixtures; deterministic checkpoint schedules; raw mapped-atomic litmus tests;
Windows/Linux lock tracing; samples, clean consumers, Docker, and release
qualification scripts.

**Target Platform**: Windows x64 and Linux x64, including qualified same-host
Linux containers. All implementations reject other architectures until a later
feature supplies platform atomic and memory-order evidence.

**Project Type**: Multi-language reusable library monorepo producing a NuGet
package, C ABI/shared library plus CMake C++ package, and Python wheel/source
distribution with an adjacent native library.

**Performance Goals**: Preserve C# SMS2 correctness and warmed allocation gates;
complete at least 1,000,000 mixed-runtime lifecycle operations without
corruption; preserve bounded waits within the configured limit plus 250 ms;
observe no hot operation-lock acquisition; sustain multi-reader progress and
the existing absolute SMS2 latency/throughput targets without relying on a
legacy comparator.

**Constraints**: No v1 fallback or in-place conversion; no 128-bit atomics; no
mutex-backed atomic fallback; only naturally aligned lock-free 64-bit mapped
atomics; exact 33-bit slot-generation fencing; fixed slot/lease/participant
capacity; explicit recovery; trusted same-host writers; no hidden maintenance
thread; no mandatory reader copy; no direct console output; bounded
cancellation/helping; physical-creator-only initialization; cold gates held
through participant registration; current SMS2 topology and feature mask remain
unchanged.

**Scale/Scope**: Three distributions, nine ordered producer-consumer pairs, up
to 1,048,575 slots and participant records within layout limits, default 64
open handles, 6-12 primary readers, one or more publishers/removers, collision
spill/reuse, at least 1,000,000 deterministic transition repetitions, and
10,000 crash-recovery cases in release qualification.

## Constitution Check

*GATE: Passed before Phase 0 research and re-checked after Phase 1 design.*

- **Library and Package First — PASS**: all work produces independently
  consumable managed, native, and Python library packages plus minimal samples;
  test brokers and agents remain outside production packages.
- **Stable Contracts and Semantic Versioning — PASS**: the user explicitly
  authorizes breaking compatibility. NuGet advances to 3.0.0; native and Python
  packages advance to 1.0.0; the C ABI advances to 2.0/SOVERSION 2. Layout 2.0
  and resource protocol 2 remain separately versioned. Migration is documented
  as drain/close/recreate/republish and old mappings fail closed.
- **Test-Driven Production Quality — PASS**: tasks require failing API,
  conformance, atomic, state-machine, lifecycle, interop, recovery, package, and
  platform tests before the corresponding implementation. Full release tests
  and packaging are completion gates.
- **.NET 10 Baseline, Portable Core — PASS**: .NET 10 remains the primary managed
  package while the existing language-neutral SMS2 contract becomes executable
  in C++ and through Python. Platform behavior remains isolated and every
  non-portable cross-process atomic assumption has executable x64 evidence.
- **Minimal, Observable, Dependency-Conscious Design — PASS**: no runtime
  dependency is added. Python uses the already packaged native library;
  diagnostics remain caller controlled; engines have no hidden workers,
  process-wide configuration, or direct output.

The post-design re-check remains PASS. Public, protocol, packaging,
interoperability, validation, and migration contracts are explicit. The native
engine is larger than the retired v1 engine because it implements the already
approved helpable SMS2 state machines; modular responsibility files prevent
that necessary complexity from crossing ABI, language, or platform boundaries.

## Project Structure

### Documentation (this feature)

```text
specs/010-lock-free-only-multilang/
|-- spec.md
|-- plan.md
|-- research.md
|-- data-model.md
|-- quickstart.md
|-- contracts/
|   |-- public-api.md
|   |-- protocol-conformance.md
|   |-- interoperability-and-validation.md
|   `-- packaging-and-migration.md
|-- checklists/
|   |-- requirements.md
|   `-- protocol.md
`-- tasks.md
```

### Source Code (repository root)

```text
protocol/
|-- README.md
|-- layout-v2.0.md
|-- resource-naming-v2.md
|-- compatibility.json
`-- fixtures/v2.0/

src/SharedMemoryStore/
|-- MemoryStore.cs
|-- SharedMemoryStoreOptions.cs
|-- StoreProtocolInfo.cs
|-- Engines/
|   |-- IStoreEngine.cs
|   `-- StoreEngineFactory.cs
|-- LayoutV2/
|-- LockFree/
|-- Interop/
|-- Lifecycle/
|-- Diagnostics/
`-- Ingest/

src/cpp/
|-- include/shared_memory_store/
|   |-- c_api.h
|   `-- store.hpp
`-- src/
    |-- mapped_atomic.hpp
    |-- layout_v2.hpp
    |-- control_codecs.hpp
    |-- operation_budget.hpp
    |-- lifecycle_gate.hpp
    |-- cold_open.hpp
    |-- participant_registry.hpp/.cpp
    |-- key_directory.hpp/.cpp
    |-- slot_table.hpp/.cpp
    |-- lease_registry.hpp/.cpp
    |-- reclaimer.hpp/.cpp
    |-- recovery.hpp/.cpp
    |-- diagnostics.hpp/.cpp
    |-- store.cpp
    |-- c_api.cpp
    |-- platform_windows.cpp
    `-- platform_linux.cpp

src/python/shared_memory_store/
|-- __init__.py
|-- _native.py
|-- enums.py
`-- store.py

tests/
|-- SharedMemoryStore.UnitTests/
|-- SharedMemoryStore.ContractTests/
|-- SharedMemoryStore.IntegrationTests/
|-- SharedMemoryStore.LinearizabilityTests/
|-- SharedMemoryStore.InteropTests/
|-- SharedMemoryStore.LockFreeAgent/
|-- cpp/
`-- python/
```

**Structure Decision**: Keep the public C# facade and token types stable in
location while deleting their embedded legacy core. Keep the validated C# SMS2
engine as the semantic reference. Split the native port by the same volatility
boundaries rather than growing the current v1 `store.cpp`. Python remains a
binding/lifetime layer over C ABI 2.0. Current protocol documentation and
fixtures describe only SMS2; historical Spec-Kit artifacts and source history
remain untouched.

## Delivery Strategy

1. Freeze the exact SMS2 mask-7 protocol and generate complete cross-language
   fixtures for layouts, codecs, hashes, names, states, statuses, and malformed
   inputs.
2. Add breaking single-protocol C# and package contract tests, then remove
   `StoreProfile`, make ordinary sizing/creation SMS2, convert `MemoryStore` to
   an engine-only facade, move shared key/owner utilities, and delete v1 code.
3. Define C ABI 2.0 and native layout/control/atomic primitives. Require compile
   and runtime x64 lock-free checks plus raw cross-process visibility/CAS tests
   before implementing the engine.
4. Correct native cold open before hot operations: gate-before-map ordering,
   physical creation disposition, header-first actual-extent validation,
   participant registration under held gates, Linux exact owner evidence, and
   bounded reverse-order cleanup.
5. Port participant registration, operation budgets, one-key slot/publication
   lifecycle, reservations, leases, logical removal, reclamation, and disposal
   with deterministic tests preceding each module.
6. Port the fixed primary directory, overflow/spill summary, generation-tagged
   helping, stable collections, exact CAS cleanup, collision churn, and
   corruption latch without changing the mapped encoding.
7. Port exact-incarnation reservation/lease recovery and bounded diagnostics,
   then complete C ABI/RAII ownership and same-handle concurrency.
8. Retarget Python constants, structures, context managers, views, and loader to
   ABI 2.0/SMS2; narrow its local lock to an operation-entry lifetime gate so
   native calls can progress concurrently.
9. Replace v1 rejection/profile tests, samples, benchmarks, scripts, and active
   docs with SMS2 conformance, all nine positive interop pairs, pause/crash/
   recovery/corruption schedules, clean consumers, and absolute qualification.
10. Run full managed/native/Python/interop/Docker/platform/package validation,
    run Spec-Kit convergence, implement any appended tasks, and repeat until the
    code and artifacts converge with every task checked.

## Semantic Version and Deployment

- **Mapped protocol**: layout 2.0 `SMS2`, required features `7`; unchanged.
- **Resource protocol**: version 2; unchanged and used by every runtime.
- **NuGet**: 3.0.0 because public profile symbols and default layout behavior
  are removed.
- **Native package**: 1.0.0; **C ABI**: 2.0 (`0x00020000`); Linux SOVERSION 2.
- **Python package**: 1.0.0 and requires exactly the packaged C ABI major 2.
- **Deployment**: stop writers/readers, close every legacy handle, remove or let
  the retired mapping lifecycle complete, deploy current packages, recreate the
  store, and republish application-owned values. No binary reads, conversion,
  fallback, mixed old/new deployment, or rollback to a live SMS2 store is
  promised.

## Risks and Weak Seams

- The native SMS2 port is a large concurrency implementation. Superficial
  happy-path parity can hide ABA, tentative-publication, overflow-summary,
  stable-scan, or recovery errors; deterministic transition tests and faithful
  codec/state-machine correspondence are mandatory.
- Cross-process C++ atomics are platform/toolchain-qualified rather than purely
  guaranteed by the ISO language model. Unsupported or non-lock-free toolchains
  fail at configure/open and never substitute a mutex.
- Cold lifecycle errors can delete live resources or double-initialize a region
  even if the hot engine is correct. Cold-open tests precede data operations.
- A broad Python per-handle lock would mask native races and violate intended
  same-handle concurrency. Python retains only local lifetime coordination.
- Deleting legacy tests indiscriminately would discard public behavioral
  coverage. General tests are retargeted to SMS2; only v1 topology and profile
  comparison assertions are removed.
- Resource-naming-v2 currently inherits parts of v1 guidance. Those rules must
  be copied/consolidated before active v1 documents are removed.

## Complexity Tracking

No constitution violation is planned. Three public distributions are required
by the explicit interoperability goal. The modular native engine mirrors
independent protocol responsibilities; a smaller globally locked engine is not
an acceptable alternative because it contradicts the sole lock-free protocol.

## Phase 0 Research Summary

See [research.md](research.md). All technical unknowns are resolved: SMS2 mask 7
stays unchanged; native code ports the validated state machines; Python binds
the native core; only qualified lock-free 64-bit mapped atomics are accepted;
cold lifecycle is corrected before hot operations; ABI/profile compatibility is
intentionally broken; and one canonical fixture/qualification source governs
all distributions.

## Phase 1 Design Summary

See [data-model.md](data-model.md), [contracts](contracts/), and
[quickstart.md](quickstart.md). Protocol entities and state transitions remain
owned by layout 2.0, public language contracts adapt equivalent lifetimes,
packaging and migration are explicit, and cross-runtime validation supplies the
acceptance evidence before implementation tasks are generated.

