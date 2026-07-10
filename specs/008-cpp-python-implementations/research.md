# Research: Native and Python Implementations

## Decision 1: Keep the implementations in one repository

**Decision**: Add C++ and Python as independently packaged siblings of the
existing C# implementation, with one root protocol and interoperability suite.

**Rationale**: The three distributions participate in the same mapped bytes,
locks, resource names, and lifecycle. A protocol change must update fixtures and
all participants atomically while the project has one owner.

**Alternatives considered**: Separate language repositories were rejected for
the initial implementation because they would require synchronized protocol PRs
before ownership and release cadence justify that cost. Moving the C# source was
rejected because it creates unrelated churn.

## Decision 2: One native core and one stable C ABI

**Decision**: Implement the state machine and platform mechanisms once in C++20.
Expose fixed-width structs, enums, byte pointers, lengths, and opaque handles
through `extern "C"`. Build an ergonomic C++ RAII wrapper over that ABI.

**Rationale**: A C ABI avoids exported standard-library types, exception
boundaries, compiler-specific name mangling, and ownership ambiguity. The C++
wrapper remains pleasant for native users without becoming the Python contract.

**Alternatives considered**: Exporting a C++ ABI was rejected as compiler and
standard-library fragile. Reimplementing the state machine separately in C and
C++ was rejected as duplication.

## Decision 3: Python uses the standard library foreign-function interface

**Decision**: Use Python `ctypes` over the C ABI, with context-managed store,
lease, and reservation objects. Support Python 3.10 or newer and create borrowed
`memoryview` objects whose Python owner retains the native lease or reservation.

**Rationale**: `ctypes` loads C-compatible shared libraries without a CPython
extension or runtime dependency. A shared library that does not touch the Python
ABI can use one platform wheel across supported Python 3 versions. Current
official guidance explicitly documents both C-compatible calls and packaging a
CMake-built library beside a `ctypes` wrapper:
[Python ctypes](https://docs.python.org/3/library/ctypes.html),
[scikit-build-core ctypes guide](https://scikit-build-core.readthedocs.io/en/latest/guide/ctypes.html).

**Alternatives considered**: pybind11/nanobind and the CPython C API were
rejected because they add a runtime-specific extension ABI and extra build
surface. A pure-Python protocol implementation was rejected because it would
duplicate atomic lifecycle, mapping, recovery, and platform-lock correctness.

## Decision 4: Preserve layout v1.2 and make its hidden details canonical

**Decision**: Do not change mapped records. Publish exact sizes, offsets,
alignment, state/status numbers, FNV-1a vectors, layout calculations, and binary
fixtures under `protocol/`.

**Rationale**: Existing code and mappings use header 160 bytes, index header 32,
slot metadata 72, and lease record 40 with pack 8. Existing narrative contracts
omit some exact offsets and one ingest document still names stale minor version
1. Interoperability requires executable precision.

**Alternatives considered**: A new layout adding process start tokens was
rejected for this feature because it would invalidate existing mappings and
expand the C# compatibility change. Lease/reservation recovery therefore keeps
the current PID-based liveness contract; Linux resource owner sidecars retain
their stronger PID-plus-start-token check.

## Decision 5: Match the full platform-resource protocol

**Decision**: On Windows use the exact public mapping name and derived
`Local\\`/`Global\\SharedMemoryStore-*` mutex. On Linux reproduce SHA-256-derived
paths, permissions, owner records, atomic owner-file replacement, lifecycle
locking, and nonblocking `fcntl` byte-range locks over `[0,1)` plus a
process-local per-path mutex.

**Rationale**: Layout compatibility alone is insufficient. A foreign process
that omits owner registration can have its region deleted by a later C# opener;
using `flock` would not contend with .NET file-region locks.

**Alternatives considered**: New native-only resource names and `flock` were
rejected because C# and native processes would not synchronize or share cleanup.

## Decision 6: Use CMake and scikit-build-core only at build time

**Decision**: Use target-based CMake installation/export for the native library.
Use root-level scikit-build-core packaging to build and place the shared library
inside the Python package. End users receive platform wheels and need no compiler.

**Rationale**: CMake supports portable target installation and exports, while
Python packaging requires platform-specific wheels for bundled compiled code:
[CMake installation guide](https://cmake.org/cmake/help/latest/guide/tutorial/Installation%20Commands%20and%20Concepts.html),
[Python packaging flow](https://packaging.python.org/en/latest/flow/).

**Alternatives considered**: A nested Python project was rejected because its
source distribution cannot naturally include sibling C++ sources. Hand-written
wheel construction was rejected as unnecessary packaging risk.

## Decision 7: Use layered conformance and live interoperability tests

**Decision**: Add static ABI assertions, canonical JSON/binary fixtures,
dependency-free C++ tests, Python unit tests, and JSON-lines subprocess agents
orchestrated by xUnit. Run the ordered 3x3 producer-consumer matrix and mixed
lease, reservation, removal, contention, ownership, and crash-recovery cases on
Windows and Linux.

**Rationale**: Fixtures detect encoding drift; live tests detect incompatible
locks, cleanup, and memory-lifetime behavior that fixtures cannot exercise.

**Alternatives considered**: Testing each implementation independently was
rejected because all could reproduce the same mistaken interpretation. Binary
fixtures alone were rejected because platform lifecycle is outside the mapping.

## Decision 8: Validate with available and reproducible toolchains

**Decision**: Make CMake/CTest the canonical build, provide PowerShell wrappers,
and add a Linux toolchain container for repeatable local validation. Validate
native Windows with a portable or installed standards-compliant compiler and in
Windows CI.

**Rationale**: The current host has .NET 10, Python 3.14, Docker, and WSL g++ but
no native Windows compiler or CMake. The repository must make missing build
prerequisites explicit rather than silently skipping native validation.

**Alternatives considered**: Treating uncompiled C++ as complete was rejected.
Installing a required system-wide compiler implicitly was rejected in favor of
workspace-local tooling or documented CI/toolchain prerequisites.
