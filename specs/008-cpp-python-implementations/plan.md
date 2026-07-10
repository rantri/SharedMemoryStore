# Implementation Plan: Native and Python Implementations

**Branch**: `codex/cpp-python-implementations` | **Date**: 2026-07-10 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/008-cpp-python-implementations/spec.md`

## Summary

Deliver interoperable C++ and Python distributions in the existing monorepo
without changing the current C# API or layout-v1.2 mapped records. A C++20 core
will own the protocol algorithms and Linux/Windows mechanisms. A fixed-width,
opaque-handle C ABI will be the binary boundary; the public C++ API will wrap it
with RAII, and the Python package will call it through the standard library's
foreign-function interface. Shared protocol vectors, exact ABI assertions, and
mixed-process tests will make the on-memory and platform-resource contracts
executable across all three runtimes.

## Technical Context

**Language/Version**: Existing C# on .NET 10; C++20 core with a C-compatible ABI;
Python 3.10 or newer.

**Primary Dependencies**: Runtime dependencies are the platform OS APIs, C++
standard library, and Python standard library only. CMake 3.20 or newer and
scikit-build-core are build-only dependencies. Existing xUnit remains the
cross-process orchestration dependency for the repository test suite.

**Storage**: Existing layout major 1, minor 2 named memory mapping. The mapped
region remains little-endian and contains the 160-byte header, open-addressed
key index, lease registry, 72-byte slot records, descriptor storage, and payload
storage. Linux also uses deterministic region, lock, owner, and lifecycle files;
Windows uses a named mapping and named mutex.

**Testing**: Dependency-free C++ assertions run through CTest; Python `unittest`;
existing .NET unit/contract/integration tests; exact JSON and binary protocol
fixtures; and a new xUnit subprocess interoperability harness covering C#,
C++, and Python agents.

**Target Platform**: Little-endian 64-bit Linux and Windows hosts, with x64 as
the required release-validation architecture. Linux-based same-host Docker
remains supported when IPC, PID, identity, permission, and capacity requirements
are met. The code uses fixed-width ABI fields so additional little-endian
architectures can be validated without changing the contract.

**Project Type**: Monorepo containing independently consumable NuGet, CMake, and
Python library distributions plus shared protocol artifacts and tests.

**Performance Goals**: Preserve caller-bounded waits within the selected limit
plus 250 milliseconds; pass 1,000-value ordered producer-consumer scenarios;
complete 10,000 mixed lifecycle/recovery cycles per supported environment; and
retain the existing managed long-running churn validation.

**Constraints**: Preserve the existing C# public API and layout v1.2; never pass
C++ exceptions, standard-library types, platform-sized integers, or ownership
ambiguity across the C ABI; keep runtime dependencies minimal; no hidden workers,
global mutable configuration, direct console output, cross-host semantics,
persistence guarantee, or malicious-writer protection.

**Scale/Scope**: Core create/open, publish, segmented publish, acquire/release,
remove/reuse, reservation/advance/commit/abort, explicit lease and reservation
recovery, diagnostics, bounded waits, platform ownership/cleanup, samples,
packaging, and all ordered language pairings.

## Constitution Check

*GATE: Passed before Phase 0 research and re-checked after Phase 1 design.*

- Library and package first: PASS. The primary NuGet artifact remains intact;
  native and Python outputs are independently consumable libraries rather than
  application wrappers.
- Stable contracts and semantic versioning: PASS. Layout v1.2 remains unchanged,
  the new C ABI is explicitly versioned, package versions remain independent,
  and cross-runtime compatibility is covered by executable contracts.
- Test-driven production quality: PASS. Exact layout, API, ownership, lifetime,
  contention, crash recovery, package consumption, and every ordered runtime
  pairing have planned automated coverage.
- .NET 10 baseline and portable core: PASS. Existing .NET behavior stays the
  baseline; new platform and language mechanisms conform to its documented
  protocol rather than redefining it.
- Minimal, observable, dependency-conscious design: PASS. The native and Python
  runtime layers add no third-party runtime dependencies, diagnostics remain
  caller-controlled, and no hidden background work is introduced.

## Project Structure

### Documentation (this feature)

```text
specs/008-cpp-python-implementations/
|-- spec.md
|-- plan.md
|-- research.md
|-- data-model.md
|-- quickstart.md
|-- contracts/
|   |-- native-c-api.md
|   |-- cpp-api.md
|   |-- python-api.md
|   |-- interoperability.md
|   `-- packaging.md
|-- checklists/
|   `-- requirements.md
`-- tasks.md
```

### Source Code (repository root)

```text
CMakeLists.txt
pyproject.toml
cmake/
|-- SharedMemoryStoreConfig.cmake.in
`-- toolchain-independent package helpers
protocol/
|-- README.md
|-- layout-v1.2.md
|-- resource-naming-v1.md
|-- compatibility.json
`-- fixtures/v1.2/
    |-- manifest.json
    `-- representative mapped-region binaries
src/
|-- SharedMemoryStore/                  # Existing C# implementation, unchanged location
|-- cpp/
|   |-- include/shared_memory_store/
|   |   |-- c_api.h
|   |   `-- store.hpp
|   `-- src/
|       |-- c_api.cpp
|       |-- store.cpp
|       |-- layout.hpp
|       |-- sha256.cpp
|       |-- platform_linux.cpp
|       `-- platform_windows.cpp
`-- python/
    `-- shared_memory_store/
        |-- __init__.py
        |-- _native.py
        |-- enums.py
        `-- store.py
tests/
|-- cpp/
|   |-- CMakeLists.txt
|   `-- *_tests.cpp
|-- python/
|   `-- test_*.py
|-- SharedMemoryStore.InteropAgent/     # JSON-lines C# subprocess participant
`-- SharedMemoryStore.InteropTests/     # Cross-runtime orchestrator
samples/
|-- CppBasicUsage/
`-- PythonBasicUsage/
scripts/
|-- validate-native.ps1
|-- validate-python.ps1
`-- validate-interoperability.ps1
```

**Structure Decision**: Keep the current C# source tree stable and add language
siblings beneath `src/`. Put the authoritative language-neutral protocol,
fixtures, and compatibility matrix at repository root because every distribution
depends on them. Keep a root `pyproject.toml` so a Python source distribution can
legally include the sibling native sources it must build. The C++ implementation
owns mechanisms; C++ and Python ergonomics depend on the same C ABI.

## Dependency Direction

```text
Python API --> ctypes declarations --> versioned C ABI --> C++ protocol core --> OS adapter
C++ RAII API --------------------------^                --> shared protocol fixtures
C# implementation -------------------------------------> shared protocol fixtures
Interop agents ----------------------------------------> public APIs only
```

The C ABI must not depend on Python or expose the C++ ABI. Platform adapters must
not know about Python or C++ ergonomic wrappers. All implementations depend on
the protocol; the protocol never depends on an implementation.

## Change Impact Analysis

- Layout or state change: updates the canonical protocol, all implementations,
  every fixture, compatibility metadata, and full cross-runtime matrix. This is
  a separately versioned compatibility event.
- Python-only API change: remains above the C ABI and affects Python tests and
  packaging only.
- C++ ergonomic API change: remains in the RAII wrapper unless it requires a new
  C ABI capability.
- Platform resource or locking change: affects the relevant C# and C++ adapters
  and the cross-runtime ownership/contention tests, but not public wrappers.
- Throughput or allocation optimization: stays inside one core when layout,
  state visibility, lifetimes, and status outcomes remain unchanged.

## Complexity Tracking

No constitution violations are planned. Multiple distributions and test agents
are required by the explicit cross-language interoperability goal; they remain
one repository because protocol changes require atomic review and validation.

## Phase 0 Research Summary

See [research.md](research.md). All technical unknowns are resolved.

## Phase 1 Design Summary

See [data-model.md](data-model.md), the contracts under [contracts/](contracts/),
and [quickstart.md](quickstart.md). The post-design constitution check remains
PASS: layout v1.2 is preserved, the new ABI is isolated and versioned, runtime
dependencies remain minimal, ownership is explicit, and validation covers both
offline bytes and live cross-process mechanisms.
